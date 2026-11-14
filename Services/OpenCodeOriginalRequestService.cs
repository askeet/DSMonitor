using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DSMonitor.Services;

/// <summary>
/// Result state for the selected job's original request. Only a singleton,
/// explicitly verified correlation, or uniquely nearest-timestamp message
/// carries text; every other state keeps the display field empty.
/// </summary>
public enum OriginalRequestStatus
{
    None,
    BestEffortSingleton,
    VerifiedJobCorrelation,
    ObservedJobCorrelation,

    /// <summary>
    /// Provisional nearest-timestamp attribution before assistant evidence exists.
    /// </summary>
    BestEffortTimeCorrelation,
    Ambiguous,
    Unavailable
}

public enum OriginalRequestUnavailableReason
{
    None,
    InvalidIdentifier,
    ProcessFailure,
    TimedOut,
    ResponseLimitExceeded,
    TextLimitExceeded,
    MalformedResponse,
    DuplicateMessage,
    PaginationLimit,
    PaginationInconsistent,
    InvalidJobTimestamp,
    UnexpectedFailure
}

/// <summary>
/// The only value passed from the read-only OpenCode observation into the UI.
/// The text is exact and untruncated; no diagnostic or source response content
/// is retained for logs.
/// </summary>
public sealed record OpenCodeOriginalRequestResult
{
    public required OriginalRequestStatus Status { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public OriginalRequestUnavailableReason Reason { get; init; }

    public bool IsSuccess =>
        Status is OriginalRequestStatus.BestEffortSingleton or
            OriginalRequestStatus.VerifiedJobCorrelation or
            OriginalRequestStatus.ObservedJobCorrelation or
            OriginalRequestStatus.BestEffortTimeCorrelation;

    public static OpenCodeOriginalRequestResult BestEffortSingleton(string text) => new()
    {
        Status = OriginalRequestStatus.BestEffortSingleton,
        Text = text,
        Label = "Best-effort singleton — exactly one user message in the verified session; " +
                "the request may belong to another job sharing this session."
    };

    public static OpenCodeOriginalRequestResult VerifiedJobCorrelation(string text) => new()
    {
        Status = OriginalRequestStatus.VerifiedJobCorrelation,
        Text = text,
        Label = "Verified job-specific correlation."
    };

    /// <summary>
    /// A user message inferred from one or more observed assistant message ids
    /// in the same verified session. Provider message order, not timestamps,
    /// supplies the preceding user; this is an inference, not authoritative
    /// ownership verification.
    /// </summary>
    public static OpenCodeOriginalRequestResult ObservedJobCorrelation(string text) => new()
    {
        Status = OriginalRequestStatus.ObservedJobCorrelation,
        Text = text,
        Label = "Observed message correlation — matched this job's verified Live Activity " +
                "assistant message ids to the latest preceding user message in provider order " +
                "for the verified session; this is an inference from message order, not " +
                "authoritative proof of job ownership."
    };

    /// <summary>
    /// Provisional attribution based on the nearest request timestamp.
    /// </summary>
    public static OpenCodeOriginalRequestResult BestEffortTimeCorrelation(string text) => new()
    {
        Status = OriginalRequestStatus.BestEffortTimeCorrelation,
        Text = text,
        Label = "Best-effort time attribution (provisional) — the user message whose timestamp is uniquely " +
                "closest to the Forge job start; a timestamp heuristic, not verified job ownership."
    };

    public static OpenCodeOriginalRequestResult Ambiguous() => new()
    {
        Status = OriginalRequestStatus.Ambiguous,
        Label = "Ambiguous — no unique exact observed request correlation."
    };

    public static OpenCodeOriginalRequestResult None() => new()
    {
        Status = OriginalRequestStatus.None
    };

    public static OpenCodeOriginalRequestResult Unavailable(OriginalRequestUnavailableReason reason) => new()
    {
        Status = OriginalRequestStatus.Unavailable,
        Reason = reason
    };
}

public interface IOriginalRequestService : IDisposable
{
    Task<OpenCodeOriginalRequestResult> GetOriginalRequestAsync(
        string jobId,
        string sessionId,
        string? verifiedUserMessageId,
        CancellationToken cancellationToken,
        DateTimeOffset? jobStartedAt = null,
        IReadOnlyList<string>? observedAssistantMessageIds = null);

    void InvalidateJob(string jobId);

    void ClearCache();
}

/// <summary>Safe no-op used when original-request observation is not wired.</summary>
public sealed class DisabledOriginalRequestService : IOriginalRequestService
{
    public static readonly DisabledOriginalRequestService Instance = new();

    private DisabledOriginalRequestService()
    {
    }

    public Task<OpenCodeOriginalRequestResult> GetOriginalRequestAsync(
        string jobId,
        string sessionId,
        string? verifiedUserMessageId,
        CancellationToken cancellationToken,
        DateTimeOffset? jobStartedAt = null,
        IReadOnlyList<string>? observedAssistantMessageIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenCodeOriginalRequestResult.None());
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

/// <summary>Bound and timeout policy for the read-only OpenCode CLI query.</summary>
public sealed record OpenCodeOriginalRequestOptions
{
    public static readonly OpenCodeOriginalRequestOptions Default = new();

    public string Executable { get; init; } = "opencode";
    public int PageSize { get; init; } = 50;
    public int MaxPages { get; init; } = 20;
    public int MaxResponseCharacters { get; init; } = 1_000_000;
    public int MaxTextCharacters { get; init; } = 512_000;
    public int MaxCachedCharacters { get; init; } = 2_000_000;
    public int MaxCacheEntries { get; init; } = 128;
    public int MaxStderrCharacters { get; init; } = 32_768;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable))
        {
            throw new ArgumentException("An executable name is required.", nameof(Executable));
        }

        if (PageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        if (MaxPages is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPages));
        }

        if (MaxResponseCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseCharacters));
        }

        if (MaxTextCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTextCharacters));
        }

        if (MaxCachedCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCachedCharacters));
        }

        if (MaxCacheEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCacheEntries));
        }

        if (MaxStderrCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStderrCharacters));
        }

        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (OperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        }
    }
}

/// <summary>One bounded CLI invocation. No standard error content is retained.</summary>
public sealed record OpenCodeCliExecution(
    int ExitCode,
    string StandardOutput,
    bool OutputLimitExceeded,
    bool TimedOut,
    bool StandardErrorPresent,
    bool FailedToStart,
    bool ReadFailed = false);

public interface IOpenCodeCliExecutor
{
    Task<OpenCodeCliExecution> ExecuteAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes exactly one read-only <c>opencode api get</c> invocation. Arguments
/// are passed through <see cref="ProcessStartInfo.ArgumentList"/> and no shell,
/// command file, prompt, or session-control action is used.
/// </summary>
public sealed class OpenCodeCliProcessExecutor : IOpenCodeCliExecutor
{
    private readonly OpenCodeOriginalRequestOptions _options;

    public OpenCodeCliProcessExecutor(OpenCodeOriginalRequestOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<OpenCodeCliExecution> ExecuteAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        if (startInfo.UseShellExecute ||
            !startInfo.RedirectStandardInput ||
            !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError)
        {
            return new OpenCodeCliExecution(-1, string.Empty, false, false, false, true);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = false
        };

        try
        {
            if (!process.Start())
            {
                return new OpenCodeCliExecution(-1, string.Empty, false, false, false, true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OpenCodeCliExecution(-1, string.Empty, false, false, false, true);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The process may already have exited; stdout/stderr still bound the call.
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.CommandTimeout);

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            _options.MaxResponseCharacters,
            () => TryKill(process),
            timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            _options.MaxStderrCharacters,
            () => TryKill(process),
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new OpenCodeCliExecution(-1, string.Empty, false, true, false, false);
        }

        BoundedRead stdout;
        BoundedRead stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new OpenCodeCliExecution(-1, string.Empty, false, true, false, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryKill(process);
            return new OpenCodeCliExecution(
                -1,
                string.Empty,
                false,
                false,
                stderrTask.IsCompletedSuccessfully,
                false,
                true);
        }

        return new OpenCodeCliExecution(
            process.ExitCode,
            stdout.Text,
            stdout.Overflow,
            false,
            stderr.Present,
            false,
            stdout.Faulted || stderr.Faulted);
    }

    private static async Task DrainAsync(params Task[] tasks)
    {
        try
        {
            // Bound shutdown even if a terminated child leaves a pipe open.
            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Draining is best effort; the process has already been terminated.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The process may have exited between the check and the kill.
        }
    }

    internal static async Task<BoundedRead> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        Action onOverflow,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 16_384));
        var buffer = new char[4_096];
        var overflow = false;
        var present = false;
        var faulted = false;

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                present = true;
                var remaining = maxCharacters - builder.Length;
                if (remaining > 0)
                {
                    var take = Math.Min(read, remaining);
                    builder.Append(buffer, 0, take);
                    if (take < read && !overflow)
                    {
                        overflow = true;
                        onOverflow();
                    }
                }
                else if (!overflow)
                {
                    overflow = true;
                    onOverflow();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A killed or closed process can surface as an I/O error. An
            // overflow already explains the termination; otherwise fail closed
            // rather than accepting a possibly truncated JSON prefix.
            if (!overflow)
            {
                faulted = true;
            }
        }

        return new BoundedRead(builder.ToString(), overflow, present, faulted);
    }

    internal sealed record BoundedRead(
        string Text,
        bool Overflow,
        bool Present,
        bool Faulted);
}

/// <summary>
/// Reads the selected job's verified OpenCode session through the read-only
/// CLI. With no observed assistant evidence it queries only user messages and
/// reads all bounded pages: exactly one user message is shown as a best-effort
/// singleton, and multiple messages are ambiguous. With observed assistant
/// message ids it reads one unfiltered ascending message stream, requires
/// complete bounded pagination and distinct ids, matches every observed id to
/// an API assistant message, and selects the latest preceding user only when
/// every matched assistant has the same predecessor. Provider order is used;
/// timestamps are never an attribution input. Contradictory evidence never
/// falls back to the singleton heuristic. Responses are bounded and transient;
/// no response body or error text is logged.
/// </summary>
public sealed class OpenCodeOriginalRequestService : IOriginalRequestService
{
    private readonly IOpenCodeCliExecutor _executor;
    private readonly OpenCodeOriginalRequestOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<CacheKey, InFlight> _inFlight = new();
    private readonly Dictionary<string, long> _jobGenerations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextGeneration;
    private long _nextOperationId;
    private long _epoch;
    private int _cachedCharacters;
    private bool _disposed;

    public OpenCodeOriginalRequestService(
        IOpenCodeCliExecutor executor,
        OpenCodeOriginalRequestOptions? options = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? OpenCodeOriginalRequestOptions.Default;
        _options.Validate();
    }

    public static OpenCodeOriginalRequestService CreateProduction()
    {
        var options = OpenCodeOriginalRequestOptions.Default;
        return new OpenCodeOriginalRequestService(
            new OpenCodeCliProcessExecutor(options),
            options);
    }

    public async Task<OpenCodeOriginalRequestResult> GetOriginalRequestAsync(
        string jobId,
        string sessionId,
        string? verifiedUserMessageId,
        CancellationToken cancellationToken,
        DateTimeOffset? jobStartedAt = null,
        IReadOnlyList<string>? observedAssistantMessageIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OpenCodeIdentifiers.IsSafeJobId(jobId) ||
            !OpenCodeIdentifiers.IsSafeSessionId(sessionId) ||
            (verifiedUserMessageId is not null &&
             !OpenCodeIdentifiers.IsSafeMessageId(verifiedUserMessageId)))
        {
            return OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.InvalidIdentifier);
        }

        if (!TryNormalizeObservedIds(
                observedAssistantMessageIds,
                out var observedIds,
                out var evidenceFailure))
        {
            return evidenceFailure;
        }

        var evidenceFingerprint = OriginalRequestEvidence.ComputeFingerprint(
            jobId,
            sessionId,
            observedIds);
        var key = new CacheKey(
            jobId,
            sessionId,
            verifiedUserMessageId ?? string.Empty,
            evidenceFingerprint,
            jobStartedAt?.UtcTicks);
        Task<OpenCodeOriginalRequestResult> task;
        lock (_gate)
        {
            if (_disposed)
            {
                return OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.UnexpectedFailure);
            }

            if (TryGetCached(key, out var cached))
            {
                return cached;
            }

            if (_inFlight.TryGetValue(key, out var existing))
            {
                task = existing.Task;
            }
            else
            {
                var generation = CurrentGeneration(jobId);
                var operationId = ++_nextOperationId;
                var inFlight = new InFlight(operationId, generation);
                var lazy = new Lazy<Task<OpenCodeOriginalRequestResult>>(
                    () => ExecuteAndCacheAsync(key, inFlight, observedIds),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                inFlight.Task = lazy.Value;
                _inFlight[key] = inFlight;
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
        out OpenCodeOriginalRequestResult failure)
    {
        normalized = Array.Empty<string>();
        failure = null!;
        if (input is null || input.Count == 0)
        {
            return true;
        }

        if (input.Count > OriginalRequestEvidence.MaxMessageIds)
        {
            failure = OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.InvalidIdentifier);
            return false;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in input)
        {
            if (!OpenCodeIdentifiers.IsSafeMessageId(id))
            {
                failure = OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.InvalidIdentifier);
                return false;
            }

            if (!set.Add(id!))
            {
                // Duplicate observed evidence is contradictory, not an
                // invitation to fall back to a singleton guess.
                failure = OpenCodeOriginalRequestResult.Ambiguous();
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
            _cachedCharacters = 0;
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
            _cachedCharacters = 0;
            _inFlight.Clear();
        }

        _lifetime.Cancel();
    }

    private async Task<OpenCodeOriginalRequestResult> ExecuteAndCacheAsync(
        CacheKey key,
        InFlight inFlight,
        IReadOnlyList<string> observedIds)
    {
        // Yield so the caller can publish the in-flight entry before a
        // synchronously completing test executor re-enters the cache lock.
        await Task.Yield();

        OpenCodeOriginalRequestResult result;
        try
        {
            result = await FetchAsync(key, observedIds, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never surface exception text, CLI output, or request content.
            result = OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.UnexpectedFailure);
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var current) &&
                current.OperationId == inFlight.OperationId)
            {
                _inFlight.Remove(key);
            }

            if (!_disposed &&
                result.IsSuccess &&
                result.Status != OriginalRequestStatus.BestEffortTimeCorrelation &&
                CurrentGeneration(key.JobId) == inFlight.Generation)
            {
                Store(key, result);
            }
        }

        return result;
    }

    private async Task<OpenCodeOriginalRequestResult> FetchAsync(
        CacheKey key,
        IReadOnlyList<string> observedIds,
        CancellationToken lifetimeToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        operationCts.CancelAfter(_options.OperationTimeout);
        var operationToken = operationCts.Token;

        return observedIds.Count > 0 && key.VerifiedMessageId.Length == 0
            ? await FetchObservedAsync(key, observedIds, operationToken, lifetimeToken)
                .ConfigureAwait(false)
            : await FetchUserOnlyAsync(key, operationToken, lifetimeToken)
                .ConfigureAwait(false);
    }

    private async Task<OpenCodeOriginalRequestResult> FetchUserOnlyAsync(
        CacheKey key,
        CancellationToken operationToken,
        CancellationToken lifetimeToken)
    {
        string? cursor = null;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var totalMessages = 0;
        string? matchedText = null;
        var matchCount = 0;
        long? nearestDistance = null;
        string? nearestText = null;
        var tied = false;
        var invalidTime = false;

        for (var page = 0; page < _options.MaxPages; page++)
        {
            var read = await ExecutePageAsync(
                    key.SessionId,
                    cursor,
                    userOnly: true,
                    operationToken,
                    lifetimeToken)
                .ConfigureAwait(false);
            if (read.Error is { } error)
            {
                return error;
            }

            var parsed = read.UserPage!.Value;
            if (!TryValidatePagination(parsed.Next, page, seenCursors, out error))
            {
                return error;
            }

            foreach (var message in parsed.Messages)
            {
                if (!seenIds.Add(message.Id))
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.DuplicateMessage);
                }

                totalMessages++;
                if (key.VerifiedMessageId.Length > 0)
                {
                    if (string.Equals(message.Id, key.VerifiedMessageId, StringComparison.Ordinal))
                    {
                        matchCount++;
                        if (message.Text.Length > _options.MaxTextCharacters)
                        {
                            return OpenCodeOriginalRequestResult.Unavailable(
                                OriginalRequestUnavailableReason.TextLimitExceeded);
                        }

                        matchedText = message.Text;
                    }
                }
                if (key.VerifiedMessageId.Length == 0 && key.StartTicks is { } startTicks)
                {
                    if (message.Created is not { } created)
                    {
                        invalidTime = true;
                    }
                    else
                    {
                        var distance = Math.Abs(created.UtcTicks - startTicks);
                        if (nearestDistance is null || distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestText = message.Text;
                            tied = false;
                        }
                        else if (distance == nearestDistance)
                        {
                            tied = true;
                        }
                    }
                }
            }

            if (parsed.Next is null)
            {
                return ResolveUserOnly(
                    key,
                    totalMessages,
                    matchCount,
                    matchedText,
                    nearestText,
                    invalidTime || tied,
                    _options.MaxTextCharacters);
            }

            cursor = parsed.Next;
        }

        return OpenCodeOriginalRequestResult.Unavailable(
            OriginalRequestUnavailableReason.PaginationLimit);
    }

    private async Task<OpenCodeOriginalRequestResult> FetchObservedAsync(
        CacheKey key,
        IReadOnlyList<string> observedIds,
        CancellationToken operationToken,
        CancellationToken lifetimeToken)
    {
        string? cursor = null;
        var anchors = new HashSet<string>(observedIds, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var predecessors = new Dictionary<string, string?>(StringComparer.Ordinal);
        var users = new Dictionary<string, UserMessage>(StringComparer.Ordinal);
        string? lastUserId = null;

        for (var page = 0; page < _options.MaxPages; page++)
        {
            var read = await ExecutePageAsync(
                    key.SessionId,
                    cursor,
                    userOnly: false,
                    operationToken,
                    lifetimeToken)
                .ConfigureAwait(false);
            if (read.Error is { } error)
            {
                return error;
            }

            var parsed = read.UnfilteredPage!.Value;
            if (!TryValidatePagination(parsed.Next, page, seenCursors, out error))
            {
                return error;
            }

            foreach (var message in parsed.Messages)
            {
                if (!seenIds.Add(message.Id))
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.DuplicateMessage);
                }

                if (string.Equals(message.Type, "user", StringComparison.Ordinal))
                {
                    users[message.Id] = new UserMessage(
                        message.Id,
                        message.Text!,
                        message.Created);
                    lastUserId = message.Id;
                }
                else if (string.Equals(message.Type, "assistant", StringComparison.Ordinal) &&
                         anchors.Contains(message.Id))
                {
                    // Provider stream order supplies the preceding user. No
                    // timestamp comparison is made.
                    if (!predecessors.TryAdd(message.Id, lastUserId))
                    {
                        return OpenCodeOriginalRequestResult.Unavailable(
                            OriginalRequestUnavailableReason.DuplicateMessage);
                    }
                }
            }

            if (parsed.Next is null)
            {
                return ResolveObserved(
                    anchors,
                    predecessors,
                    users,
                    _options.MaxTextCharacters);
            }

            cursor = parsed.Next;
        }

        return OpenCodeOriginalRequestResult.Unavailable(
            OriginalRequestUnavailableReason.PaginationLimit);
    }

    private async Task<PageExecution> ExecutePageAsync(
        string sessionId,
        string? cursor,
        bool userOnly,
        CancellationToken operationToken,
        CancellationToken lifetimeToken)
    {
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var startInfo = CreateStartInfo(sessionId, cursor, userOnly);
            var execution = await _executor
                .ExecuteAsync(startInfo, operationToken)
                .WaitAsync(operationToken)
                .ConfigureAwait(false);

            if (execution.TimedOut)
            {
                return PageExecution.FromReason(OriginalRequestUnavailableReason.TimedOut);
            }

            if (execution.ReadFailed)
            {
                return PageExecution.FromReason(OriginalRequestUnavailableReason.ProcessFailure);
            }

            if (execution.OutputLimitExceeded ||
                execution.StandardOutput.Length > _options.MaxResponseCharacters)
            {
                return PageExecution.FromReason(
                    OriginalRequestUnavailableReason.ResponseLimitExceeded);
            }

            if (execution.FailedToStart || execution.ExitCode != 0)
            {
                return PageExecution.FromReason(OriginalRequestUnavailableReason.ProcessFailure);
            }

            if (userOnly)
            {
                return TryParsePage(
                    execution.StandardOutput,
                    out var parsed,
                    out var parseReason)
                    ? PageExecution.FromUser(parsed)
                    : PageExecution.FromReason(parseReason);
            }

            return TryParseStreamPage(
                execution.StandardOutput,
                out var stream,
                out var streamReason)
                ? PageExecution.FromStream(stream)
                : PageExecution.FromReason(streamReason);
        }
        catch (OperationCanceledException)
        {
            if (lifetimeToken.IsCancellationRequested)
            {
                throw;
            }

            return PageExecution.FromReason(OriginalRequestUnavailableReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PageExecution.FromReason(OriginalRequestUnavailableReason.ProcessFailure);
        }
    }

    private bool TryValidatePagination(
        string? next,
        int page,
        HashSet<string> seenCursors,
        out OpenCodeOriginalRequestResult error)
    {
        error = null!;
        if (next is null)
        {
            return true;
        }

        // A repeated cursor or an exceeded page bound is an uncertain stream
        // even when the current page already proves ambiguity.
        if (!seenCursors.Add(next))
        {
            error = OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.PaginationInconsistent);
            return false;
        }

        if (page == _options.MaxPages - 1)
        {
            error = OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.PaginationLimit);
            return false;
        }

        return true;
    }

    private static OpenCodeOriginalRequestResult ResolveUserOnly(
        CacheKey key,
        int totalMessages,
        int matchCount,
        string? matchedText,
        string? nearestText,
        bool invalidOrTied,
        int maxTextCharacters)
    {
        if (totalMessages == 0)
        {
            return OpenCodeOriginalRequestResult.None();
        }

        if (key.VerifiedMessageId.Length > 0)
        {
            if (matchCount == 1 && matchedText is not null)
            {
                return OpenCodeOriginalRequestResult.VerifiedJobCorrelation(matchedText);
            }

            return totalMessages > 1
                ? OpenCodeOriginalRequestResult.Ambiguous()
                : OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.MalformedResponse);
        }

        if (key.StartTicks is not { } startTicks ||
            startTicks < DateTimeOffset.UnixEpoch.UtcTicks ||
            invalidOrTied || nearestText is null)
        {
            return OpenCodeOriginalRequestResult.Ambiguous();
        }

        return nearestText.Length > maxTextCharacters
            ? OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.TextLimitExceeded)
            : OpenCodeOriginalRequestResult.BestEffortTimeCorrelation(nearestText);
    }

    private static OpenCodeOriginalRequestResult ResolveObserved(
        IReadOnlyCollection<string> anchors,
        IReadOnlyDictionary<string, string?> predecessors,
        IReadOnlyDictionary<string, UserMessage> users,
        int maxTextCharacters)
    {
        if (anchors.Count == 0)
        {
            return OpenCodeOriginalRequestResult.Ambiguous();
        }

        string? commonUserId = null;
        foreach (var anchor in anchors)
        {
            if (!predecessors.TryGetValue(anchor, out var predecessor) ||
                predecessor is null)
            {
                // A missing anchor, a non-assistant type, or an assistant
                // before any user is contradictory evidence. Never fall back to
                // the singleton heuristic in this case.
                return OpenCodeOriginalRequestResult.Ambiguous();
            }

            if (commonUserId is null)
            {
                commonUserId = predecessor;
            }
            else if (!string.Equals(commonUserId, predecessor, StringComparison.Ordinal))
            {
                // Anchors split across requests/continuations do not identify
                // one original request.
                return OpenCodeOriginalRequestResult.Ambiguous();
            }
        }

        if (commonUserId is null ||
            !users.TryGetValue(commonUserId, out var user) ||
            user.Text is null)
        {
            return OpenCodeOriginalRequestResult.Ambiguous();
        }

        return user.Text.Length > maxTextCharacters
            ? OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.TextLimitExceeded)
            : OpenCodeOriginalRequestResult.ObservedJobCorrelation(user.Text);
    }

    private ProcessStartInfo CreateStartInfo(
        string sessionId,
        string? cursor,
        bool userOnly)
    {
        var path = "/api/session/" +
                   Uri.EscapeDataString(sessionId) +
                   "/message?";
        if (userOnly)
        {
            path += "type=user&";
        }

        path += "limit=" + _options.PageSize.ToString(CultureInfo.InvariantCulture);
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
        out ParsedPage parsed,
        out OriginalRequestUnavailableReason reason)
    {
        parsed = default;
        reason = OriginalRequestUnavailableReason.MalformedResponse;
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

            var messages = new List<UserMessage>();
            foreach (var item in data.EnumerateArray())
            {
                if (!TryParseUserMessage(item, out var message))
                {
                    return false;
                }

                messages.Add(message);
            }

            parsed = new ParsedPage(messages, next);
            reason = OriginalRequestUnavailableReason.None;
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

    private static bool TryParseStreamPage(
        string json,
        out StreamPage parsed,
        out OriginalRequestUnavailableReason reason)
    {
        parsed = default;
        reason = OriginalRequestUnavailableReason.MalformedResponse;
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

            var messages = new List<StreamMessage>();
            foreach (var item in data.EnumerateArray())
            {
                if (!TryParseStreamMessage(item, out var message))
                {
                    return false;
                }

                messages.Add(message);
            }

            parsed = new StreamPage(messages, next);
            reason = OriginalRequestUnavailableReason.None;
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

    private static bool TryParseStreamMessage(JsonElement item, out StreamMessage message)
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
        if (!string.Equals(type, "user", StringComparison.Ordinal))
        {
            // The unfiltered stream also contains assistant and other variants.
            // Their contents are deliberately not read or retained; identity
            // and type are sufficient for anchor matching and duplicate checks.
            // A time object is inspected only for duplicate "created" keys.
            if (!TryGetSingleProperty(item, "time", out var otherTime, out var hasOtherTime))
            {
                return false;
            }

            if (hasOtherTime &&
                otherTime.ValueKind == JsonValueKind.Object &&
                !TryGetSingleProperty(otherTime, "created", out _, out _))
            {
                return false;
            }

            message = new StreamMessage(id, type, null, null);
            return true;
        }

        if (!TryGetSingleProperty(item, "text", out var textElement, out var hasText) ||
            !hasText ||
            textElement.ValueKind != JsonValueKind.String ||
            !TryGetSingleProperty(item, "time", out var timeElement, out var hasTime) ||
            !hasTime ||
            timeElement.ValueKind != JsonValueKind.Object ||
            !TryGetSingleProperty(timeElement, "created", out var createdElement, out var hasCreated) ||
            !hasCreated ||
            createdElement.ValueKind != JsonValueKind.Number ||
            !createdElement.TryGetDouble(out var createdNumber) ||
            !double.IsFinite(createdNumber))
        {
            return false;
        }

        if (!TryGetSingleProperty(item, "files", out var files, out var hasFiles))
        {
            return false;
        }

        if (hasFiles && files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        DateTimeOffset? createdTimestamp =
            ExactUnixMillisecondTimestamp.TryParse(createdElement, out var exactTimestamp)
                ? exactTimestamp
                : null;
        message = new StreamMessage(
            id,
            type,
            textElement.GetString()!,
            createdTimestamp);
        return true;
    }

    /// <summary>
    /// Reads one property occurrence and rejects duplicate keys. The bounded
    /// JSON document has already been parsed; this only prevents permissive
    /// last-key-wins behavior for properties the service consumes.
    /// </summary>
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

    private static bool TryParseUserMessage(JsonElement item, out UserMessage message)
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
            !string.Equals(typeElement.GetString(), "user", StringComparison.Ordinal) ||
            !TryGetSingleProperty(item, "text", out var textElement, out var hasText) ||
            !hasText ||
            textElement.ValueKind != JsonValueKind.String ||
            !TryGetSingleProperty(item, "time", out var timeElement, out var hasTime) ||
            !hasTime ||
            timeElement.ValueKind != JsonValueKind.Object ||
            !TryGetSingleProperty(timeElement, "created", out var createdElement, out var hasCreated) ||
            !hasCreated ||
            createdElement.ValueKind != JsonValueKind.Number ||
            !createdElement.TryGetDouble(out var createdNumber) ||
            !double.IsFinite(createdNumber))
        {
            return false;
        }

        if (!TryGetSingleProperty(item, "files", out var files, out var hasFiles))
        {
            return false;
        }

        if (hasFiles && files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Timestamp precision is optional for singleton and verified-id
        // attribution. A structurally valid finite number whose exact tick is
        // unavailable stays explicit as null instead of being approximated.
        DateTimeOffset? createdTimestamp =
            ExactUnixMillisecondTimestamp.TryParse(createdElement, out var exactTimestamp)
                ? exactTimestamp
                : null;
        message = new UserMessage(
            idElement.GetString()!,
            textElement.GetString()!,
            createdTimestamp);
        return true;
    }

    private bool TryGetCached(CacheKey key, out OpenCodeOriginalRequestResult result)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            result = node.Value.Result;
            return true;
        }

        result = null!;
        return false;
    }

    private void Store(CacheKey key, OpenCodeOriginalRequestResult result)
    {
        if (result.Text.Length > _options.MaxCachedCharacters)
        {
            return;
        }

        if (_cache.TryGetValue(key, out var existing))
        {
            RemoveCached(key);
        }

        if (_cache.Count >= _options.MaxCacheEntries)
        {
            RemoveOldest();
        }

        var node = _lru.AddFirst(new CacheEntry(key, result));
        _cache[key] = node;
        _cachedCharacters += result.Text.Length;

        while (_cachedCharacters > _options.MaxCachedCharacters && _lru.Last is { } oldest)
        {
            RemoveCached(oldest.Value.Key);
        }
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
        _cachedCharacters -= node.Value.Result.Text.Length;
    }

    private Generation CurrentGeneration(string jobId) => new(
        _epoch,
        _jobGenerations.TryGetValue(jobId, out var generation) ? generation : 0);

    private readonly record struct CacheKey(
        string JobId,
        string SessionId,
        string VerifiedMessageId,
        string EvidenceFingerprint,
        long? StartTicks);

    private readonly record struct Generation(long Epoch, long JobGeneration);

    private sealed record CacheEntry(CacheKey Key, OpenCodeOriginalRequestResult Result);

    private sealed class InFlight
    {
        public InFlight(long operationId, Generation generation)
        {
            OperationId = operationId;
            Generation = generation;
        }

        public long OperationId { get; }
        public Generation Generation { get; }
        public Task<OpenCodeOriginalRequestResult> Task { get; set; } = null!;
    }

    private readonly record struct UserMessage(
        string Id,
        string Text,
        DateTimeOffset? Created);

    private readonly record struct StreamMessage(
        string Id,
        string Type,
        string? Text,
        DateTimeOffset? Created);

    private readonly record struct ParsedPage(
        IReadOnlyList<UserMessage> Messages,
        string? Next);

    private readonly record struct StreamPage(
        IReadOnlyList<StreamMessage> Messages,
        string? Next);

    private sealed record PageExecution(
        ParsedPage? UserPage,
        StreamPage? UnfilteredPage,
        OpenCodeOriginalRequestResult? Error)
    {
        public static PageExecution FromUser(ParsedPage page) =>
            new(page, null, null);

        public static PageExecution FromStream(StreamPage page) =>
            new(null, page, null);

        public static PageExecution FromReason(OriginalRequestUnavailableReason reason) =>
            new(null, null, OpenCodeOriginalRequestResult.Unavailable(reason));
    }
}

/// <summary>
/// Strict identifier boundary for values interpolated into the read-only CLI
/// path. It is deliberately ASCII-only and bounded to avoid argument, path, or
/// query injection as well as unbounded cache keys.
/// </summary>
public static class OpenCodeIdentifiers
{
    public const int MaxIdentifierLength = 200;
    private const int MaxCursorLength = 8_192;

    public static bool IsSafeJobId(string? value) =>
        IsSafeIdentifier(value, requireSessionPrefix: false);

    public static bool IsSafeSessionId(string? value) =>
        IsSafeIdentifier(value, requireSessionPrefix: true);

    public static bool IsSafeMessageId(string? value) =>
        IsSafeIdentifier(value, requireSessionPrefix: false);

    public static bool IsSafeCursor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxCursorLength)
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

    private static bool IsSafeIdentifier(string? value, bool requireSessionPrefix)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdentifierLength)
        {
            return false;
        }

        if (requireSessionPrefix &&
            !value.StartsWith("ses_", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z') &&
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= '0' and <= '9') &&
                character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
