using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DSMonitor.Services;

public enum OpenCodeActivityDurationStatus
{
    None,
    Available,
    Unavailable
}

public enum OpenCodeActivityDurationReason
{
    None,
    InvalidIdentifier,
    DuplicateEvidence,
    ProcessFailure,
    TimedOut,
    ResponseLimitExceeded,
    MalformedResponse,
    DuplicateMessage,
    PaginationLimit,
    PaginationInconsistent,
    UnexpectedFailure
}

/// <summary>
/// One completed OpenCode assistant-message duration. The value is message
/// scoped: it is not a job duration, a part duration, or completion evidence.
/// </summary>
public sealed record OpenCodeMessageDuration(
    string MessageId,
    DateTimeOffset Created,
    DateTimeOffset Completed,
    TimeSpan Duration);

/// <summary>
/// Result of one bounded read for the exact observed assistant message ids of
/// the selected job. Only exact anchor ids with a valid created/completed pair
/// are returned. Missing or invalid timing is absent rather than inferred.
/// </summary>
public sealed record OpenCodeActivityDurationsResult
{
    public required OpenCodeActivityDurationStatus Status { get; init; }
    public OpenCodeActivityDurationReason Reason { get; init; }
    public IReadOnlyDictionary<string, OpenCodeMessageDuration> Durations { get; init; } =
        new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal);

    /// <summary>
    /// True only when every requested anchor was observed as an assistant message
    /// and supplied a valid completed duration. Only complete results are cached
    /// as a whole; valid individual entries may still be retained.
    /// </summary>
    public bool IsComplete { get; init; }

    public bool IsSuccess => Status == OpenCodeActivityDurationStatus.Available;

    public static OpenCodeActivityDurationsResult None() => new()
    {
        Status = OpenCodeActivityDurationStatus.None
    };

    public static OpenCodeActivityDurationsResult Available(
        IReadOnlyDictionary<string, OpenCodeMessageDuration> durations,
        bool isComplete) => new()
    {
        Status = OpenCodeActivityDurationStatus.Available,
        Durations = durations.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal),
        IsComplete = isComplete
    };

    public static OpenCodeActivityDurationsResult Unavailable(
        OpenCodeActivityDurationReason reason) => new()
    {
        Status = OpenCodeActivityDurationStatus.Unavailable,
        Reason = reason
    };
}

public interface IOpenCodeActivityDurationService : IDisposable
{
    Task<OpenCodeActivityDurationsResult> GetDurationsAsync(
        string jobId,
        string sessionId,
        IReadOnlyList<string> observedAssistantMessageIds,
        CancellationToken cancellationToken);

    void InvalidateJob(string jobId);

    void ClearCache();
}

/// <summary>Safe no-op used when Activity History duration observation is not wired.</summary>
public sealed class DisabledOpenCodeActivityDurationService : IOpenCodeActivityDurationService
{
    public static readonly DisabledOpenCodeActivityDurationService Instance = new();

    private DisabledOpenCodeActivityDurationService()
    {
    }

    public Task<OpenCodeActivityDurationsResult> GetDurationsAsync(
        string jobId,
        string sessionId,
        IReadOnlyList<string> observedAssistantMessageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenCodeActivityDurationsResult.None());
    }

    public void InvalidateJob(string jobId)
    {
    }

    public void ClearCache()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Reads only exact observed assistant message ids through the same read-only
/// <c>opencode api get</c> surface used by the Original Request feature. It uses
/// one unfiltered ascending message stream, bounded pagination, exact ids, and
/// no text or timestamp attribution. Only assistant message time metadata for
/// the requested anchors is returned; response bodies and errors are never
/// logged or persisted.
/// </summary>
public sealed class OpenCodeActivityDurationService : IOpenCodeActivityDurationService
{
    private readonly IOpenCodeCliExecutor _executor;
    private readonly OpenCodeOriginalRequestOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<FlightKey, InFlight> _inFlight = new();
    private readonly Dictionary<string, long> _jobGenerations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextGeneration;
    private long _nextOperationId;
    private long _epoch;
    private bool _disposed;

    public OpenCodeActivityDurationService(
        IOpenCodeCliExecutor executor,
        OpenCodeOriginalRequestOptions? options = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? OpenCodeOriginalRequestOptions.Default;
        _options.Validate();
    }

    public static OpenCodeActivityDurationService CreateProduction()
    {
        var options = OpenCodeOriginalRequestOptions.Default;
        return new OpenCodeActivityDurationService(
            new OpenCodeCliProcessExecutor(options),
            options);
    }

    public async Task<OpenCodeActivityDurationsResult> GetDurationsAsync(
        string jobId,
        string sessionId,
        IReadOnlyList<string> observedAssistantMessageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OpenCodeIdentifiers.IsSafeJobId(jobId) ||
            !OpenCodeIdentifiers.IsSafeSessionId(sessionId))
        {
            return OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.InvalidIdentifier);
        }

        if (!TryNormalizeObservedIds(
                observedAssistantMessageIds,
                out var observedIds,
                out var failure))
        {
            return failure;
        }

        if (observedIds.Length == 0)
        {
            return OpenCodeActivityDurationsResult.None();
        }

        var evidenceFingerprint = OriginalRequestEvidence.ComputeFingerprint(
            jobId,
            sessionId,
            observedIds);
        var flightKey = new FlightKey(jobId, sessionId, evidenceFingerprint);
        Task<OpenCodeActivityDurationsResult> task;
        lock (_gate)
        {
            if (_disposed)
            {
                return OpenCodeActivityDurationsResult.Unavailable(
                    OpenCodeActivityDurationReason.UnexpectedFailure);
            }

            var cached = new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal);
            var allCached = true;
            foreach (var id in observedIds)
            {
                var key = new CacheKey(jobId, sessionId, id, evidenceFingerprint);
                if (TryGetCached(key, out var duration))
                {
                    cached[id] = duration;
                }
                else
                {
                    allCached = false;
                }
            }

            if (allCached)
            {
                return OpenCodeActivityDurationsResult.Available(cached, isComplete: true);
            }

            if (_inFlight.TryGetValue(flightKey, out var existing))
            {
                task = existing.Task;
            }
            else
            {
                var generation = CurrentGeneration(jobId);
                var operationId = ++_nextOperationId;
                var inFlight = new InFlight(operationId, generation);
                var lazy = new Lazy<Task<OpenCodeActivityDurationsResult>>(
                    () => ExecuteAndCacheAsync(flightKey, inFlight, observedIds),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                inFlight.Task = lazy.Value;
                _inFlight[flightKey] = inFlight;
                task = inFlight.Task;
            }
        }

        return cancellationToken.CanBeCanceled
            ? await task.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await task.ConfigureAwait(false);
    }

    private static bool TryNormalizeObservedIds(
        IReadOnlyList<string>? input,
        out string[] normalized,
        out OpenCodeActivityDurationsResult failure)
    {
        normalized = Array.Empty<string>();
        failure = null!;
        if (input is null || input.Count == 0)
        {
            return true;
        }

        if (input.Count > OriginalRequestEvidence.MaxMessageIds)
        {
            failure = OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.InvalidIdentifier);
            return false;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in input)
        {
            if (!OpenCodeIdentifiers.IsSafeMessageId(id))
            {
                failure = OpenCodeActivityDurationsResult.Unavailable(
                    OpenCodeActivityDurationReason.InvalidIdentifier);
                return false;
            }

            if (!set.Add(id!))
            {
                failure = OpenCodeActivityDurationsResult.Unavailable(
                    OpenCodeActivityDurationReason.DuplicateEvidence);
                return false;
            }
        }

        normalized = set.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return true;
    }

    public void InvalidateJob(string jobId)
    {
        if (!OpenCodeIdentifiers.IsSafeJobId(jobId))
        {
            return;
        }

        lock (_gate)
        {
            _jobGenerations[jobId] = ++_nextGeneration;
            foreach (var key in _cache.Keys.Where(key =>
                         string.Equals(key.JobId, jobId, StringComparison.Ordinal)).ToArray())
            {
                RemoveCached(key);
            }

            foreach (var key in _inFlight.Keys.Where(key =>
                         string.Equals(key.JobId, jobId, StringComparison.Ordinal)).ToArray())
            {
                _inFlight.Remove(key);
            }
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _epoch++;
            _cache.Clear();
            _lru.Clear();
            _inFlight.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            _lru.Clear();
            _inFlight.Clear();
        }

        _lifetime.Cancel();
    }

    private async Task<OpenCodeActivityDurationsResult> ExecuteAndCacheAsync(
        FlightKey flightKey,
        InFlight inFlight,
        IReadOnlyList<string> observedIds)
    {
        // Yield so the caller can publish the in-flight entry before a
        // synchronously completing test executor re-enters the cache lock.
        await Task.Yield();

        OpenCodeActivityDurationsResult result;
        try
        {
            result = await FetchAsync(
                    flightKey.SessionId,
                    observedIds,
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.UnexpectedFailure);
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(flightKey, out var current) &&
                current.OperationId == inFlight.OperationId)
            {
                _inFlight.Remove(flightKey);
            }

            if (!_disposed &&
                result.IsSuccess &&
                CurrentGeneration(flightKey.JobId) == inFlight.Generation)
            {
                foreach (var id in observedIds)
                {
                    var key = new CacheKey(
                        flightKey.JobId,
                        flightKey.SessionId,
                        id,
                        flightKey.EvidenceFingerprint);
                    if (result.Durations.TryGetValue(id, out var duration))
                    {
                        Store(key, duration);
                    }
                    else
                    {
                        RemoveCached(key);
                    }
                }
            }
        }

        return result;
    }

    private async Task<OpenCodeActivityDurationsResult> FetchAsync(
        string sessionId,
        IReadOnlyList<string> observedIds,
        CancellationToken lifetimeToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        operationCts.CancelAfter(_options.OperationTimeout);
        var operationToken = operationCts.Token;

        string? cursor = null;
        var anchors = new HashSet<string>(observedIds, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var durations = new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal);

        for (var page = 0; page < _options.MaxPages; page++)
        {
            var execution = await ExecutePageAsync(
                    sessionId,
                    cursor,
                    anchors,
                    operationToken,
                    lifetimeToken)
                .ConfigureAwait(false);
            if (execution.Error is { } error)
            {
                return error;
            }

            var parsed = execution.Page!.Value;
            if (!TryValidatePagination(parsed.Next, page, seenCursors, out error))
            {
                return error;
            }

            foreach (var message in parsed.Messages)
            {
                if (!seenIds.Add(message.Id))
                {
                    return OpenCodeActivityDurationsResult.Unavailable(
                        OpenCodeActivityDurationReason.DuplicateMessage);
                }

                if (message.Duration is { } duration)
                {
                    if (anchors.Contains(message.Id))
                    {
                        durations[message.Id] = duration;
                    }
                }
            }

            if (parsed.Next is null)
            {
                return OpenCodeActivityDurationsResult.Available(
                    durations,
                    isComplete: durations.Count == anchors.Count);
            }

            cursor = parsed.Next;
        }

        return OpenCodeActivityDurationsResult.Unavailable(
            OpenCodeActivityDurationReason.PaginationLimit);
    }

    private async Task<PageExecution> ExecutePageAsync(
        string sessionId,
        string? cursor,
        IReadOnlySet<string> anchorIds,
        CancellationToken operationToken,
        CancellationToken lifetimeToken)
    {
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var startInfo = CreateStartInfo(sessionId, cursor);
            var execution = await _executor
                .ExecuteAsync(startInfo, operationToken)
                .WaitAsync(operationToken)
                .ConfigureAwait(false);

            if (execution.TimedOut)
            {
                return PageExecution.FromReason(OpenCodeActivityDurationReason.TimedOut);
            }

            if (execution.ReadFailed)
            {
                return PageExecution.FromReason(OpenCodeActivityDurationReason.ProcessFailure);
            }

            if (execution.OutputLimitExceeded ||
                execution.StandardOutput.Length > _options.MaxResponseCharacters)
            {
                return PageExecution.FromReason(
                    OpenCodeActivityDurationReason.ResponseLimitExceeded);
            }

            if (execution.FailedToStart || execution.ExitCode != 0)
            {
                return PageExecution.FromReason(OpenCodeActivityDurationReason.ProcessFailure);
            }

            return TryParsePage(
                execution.StandardOutput,
                anchorIds,
                out var parsed,
                out var reason)
                ? PageExecution.FromPage(parsed)
                : PageExecution.FromReason(reason);
        }
        catch (OperationCanceledException)
        {
            if (lifetimeToken.IsCancellationRequested)
            {
                throw;
            }

            return PageExecution.FromReason(OpenCodeActivityDurationReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PageExecution.FromReason(OpenCodeActivityDurationReason.ProcessFailure);
        }
    }

    private bool TryValidatePagination(
        string? next,
        int page,
        HashSet<string> seenCursors,
        out OpenCodeActivityDurationsResult error)
    {
        error = null!;
        if (next is null)
        {
            return true;
        }

        if (!seenCursors.Add(next))
        {
            error = OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.PaginationInconsistent);
            return false;
        }

        if (page == _options.MaxPages - 1)
        {
            error = OpenCodeActivityDurationsResult.Unavailable(
                OpenCodeActivityDurationReason.PaginationLimit);
            return false;
        }

        return true;
    }

    private ProcessStartInfo CreateStartInfo(string sessionId, string? cursor)
    {
        var path = "/api/session/" +
                   Uri.EscapeDataString(sessionId) +
                   "/message?limit=" +
                   _options.PageSize.ToString(CultureInfo.InvariantCulture);
        path += cursor is null
            ? "&order=asc"
            : "&cursor=" + Uri.EscapeDataString(cursor);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("api");
        startInfo.ArgumentList.Add("get");
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    private static bool TryParsePage(
        string json,
        IReadOnlySet<string> anchorIds,
        out ParsedPage parsed,
        out OpenCodeActivityDurationReason reason)
    {
        parsed = default;
        reason = OpenCodeActivityDurationReason.MalformedResponse;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetSingleProperty(root, "data", out var data, out var hasData) ||
                !hasData ||
                data.ValueKind != JsonValueKind.Array ||
                !TryGetSingleProperty(root, "cursor", out var cursor, out var hasCursor) ||
                !hasCursor ||
                cursor.ValueKind != JsonValueKind.Object ||
                !TryGetSingleProperty(cursor, "previous", out var previous, out var hasPrevious) ||
                (hasPrevious &&
                 previous.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) ||
                !TryGetSingleProperty(cursor, "next", out var nextElement, out var hasNext) ||
                (hasNext &&
                 nextElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)))
            {
                return false;
            }

            var next = hasNext && nextElement.ValueKind == JsonValueKind.String
                ? nextElement.GetString()
                : null;
            if (next is not null && !OpenCodeIdentifiers.IsSafeCursor(next))
            {
                return false;
            }

            var messages = new List<PageMessage>();
            foreach (var item in data.EnumerateArray())
            {
                if (!TryParseMessage(item, anchorIds, out var message))
                {
                    return false;
                }

                messages.Add(message);
            }

            parsed = new ParsedPage(messages, next);
            reason = OpenCodeActivityDurationReason.None;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseMessage(
        JsonElement item,
        IReadOnlySet<string> anchorIds,
        out PageMessage message)
    {
        message = default;
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetSingleProperty(item, "id", out var idElement, out var hasId) ||
            !hasId ||
            idElement.ValueKind != JsonValueKind.String ||
            !OpenCodeIdentifiers.IsSafeMessageId(idElement.GetString()) ||
            !TryGetSingleProperty(item, "type", out var typeElement, out var hasType) ||
            !hasType ||
            typeElement.ValueKind != JsonValueKind.String ||
            !IsSafeMessageType(typeElement.GetString()))
        {
            return false;
        }

        var id = idElement.GetString()!;
        var type = typeElement.GetString()!;
        if (!string.Equals(type, "assistant", StringComparison.Ordinal))
        {
            // Other variants are ignored without reading their contents. They
            // still participate in duplicate-id detection.
            message = new PageMessage(id, type, null);
            return true;
        }

        OpenCodeMessageDuration? duration = null;
        if (anchorIds.Contains(id) &&
            TryGetSingleProperty(item, "time", out var time, out var hasTime) &&
            hasTime &&
            time.ValueKind == JsonValueKind.Object &&
            TryGetSingleProperty(time, "created", out var createdElement, out var hasCreated) &&
            hasCreated &&
            TryGetSingleProperty(time, "completed", out var completedElement, out var hasCompleted) &&
            hasCompleted &&
            ExactUnixMillisecondTimestamp.TryParse(createdElement, out var created) &&
            ExactUnixMillisecondTimestamp.TryParse(completedElement, out var completed))
        {
            var delta = completed - created;
            if (created >= DateTimeOffset.UnixEpoch &&
                completed >= DateTimeOffset.UnixEpoch &&
                delta >= TimeSpan.Zero)
            {
                duration = new OpenCodeMessageDuration(id, created, completed, delta);
            }
        }

        message = new PageMessage(id, type, duration);
        return true;
    }

    private static bool TryGetSingleProperty(
        JsonElement value,
        string name,
        out JsonElement result,
        out bool present)
    {
        result = default;
        present = false;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }

            count++;
            if (count > 1)
            {
                return false;
            }

            result = property.Value;
            present = true;
        }

        return true;
    }

    private static bool IsSafeMessageType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character < 0x20 || character == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetCached(CacheKey key, out OpenCodeMessageDuration duration)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            duration = node.Value.Duration;
            return true;
        }

        duration = null!;
        return false;
    }

    private void Store(CacheKey key, OpenCodeMessageDuration duration)
    {
        if (_cache.TryGetValue(key, out _))
        {
            RemoveCached(key);
        }

        if (_cache.Count >= _options.MaxCacheEntries)
        {
            RemoveOldest();
        }

        var node = _lru.AddFirst(new CacheEntry(key, duration));
        _cache[key] = node;
    }

    private void RemoveOldest()
    {
        if (_lru.Last is { } oldest)
        {
            RemoveCached(oldest.Value.Key);
        }
    }

    private void RemoveCached(CacheKey key)
    {
        if (!_cache.TryGetValue(key, out var node))
        {
            return;
        }

        _cache.Remove(key);
        _lru.Remove(node);
    }

    private Generation CurrentGeneration(string jobId) => new(
        _epoch,
        _jobGenerations.TryGetValue(jobId, out var generation) ? generation : 0);

    private readonly record struct CacheKey(
        string JobId,
        string SessionId,
        string MessageId,
        string EvidenceFingerprint);

    private readonly record struct FlightKey(
        string JobId,
        string SessionId,
        string EvidenceFingerprint);

    private readonly record struct Generation(long Epoch, long JobGeneration);

    private sealed record CacheEntry(CacheKey Key, OpenCodeMessageDuration Duration);

    private sealed class InFlight
    {
        public InFlight(long operationId, Generation generation)
        {
            OperationId = operationId;
            Generation = generation;
        }

        public long OperationId { get; }
        public Generation Generation { get; }
        public Task<OpenCodeActivityDurationsResult> Task { get; set; } = null!;
    }

    private readonly record struct PageMessage(
        string Id,
        string Type,
        OpenCodeMessageDuration? Duration);

    private readonly record struct ParsedPage(
        IReadOnlyList<PageMessage> Messages,
        string? Next);

    private sealed record PageExecution(
        ParsedPage? Page,
        OpenCodeActivityDurationsResult? Error)
    {
        public static PageExecution FromPage(ParsedPage page) => new(page, null);

        public static PageExecution FromReason(OpenCodeActivityDurationReason reason) =>
            new(null, OpenCodeActivityDurationsResult.Unavailable(reason));
    }
}
