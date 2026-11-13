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

    public static OpenCodeOriginalRequestResult BestEffortTimeCorrelation(string text) => new()
    {
        Status = OriginalRequestStatus.BestEffortTimeCorrelation,
        Text = text,
        Label = "Best-effort time attribution — the user message whose timestamp is uniquely " +
                "closest to the Forge job start; a timestamp heuristic, not verified job ownership."
    };

    public static OpenCodeOriginalRequestResult Ambiguous() => new()
    {
        Status = OriginalRequestStatus.Ambiguous,
        Label = "Ambiguous — multiple user messages and no unique verified job-specific correlation."
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
        DateTimeOffset? jobStartedAt = null);

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
        DateTimeOffset? jobStartedAt = null)
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
/// CLI. It queries only user messages, reads all bounded pages before deciding,
/// and fails closed on any uncertain response. Without a verified message id,
/// a singleton is shown as best effort; multiple messages are attributed only
/// when one timestamp is uniquely closest to the job start (no window or
/// direction restriction). A successful singleton, verified correlation, or
/// time correlation is cached in bounded memory keyed by job, session, and
/// exact start time; transient failures and ambiguous results are never cached.
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
        DateTimeOffset? jobStartedAt = null)
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

        var key = new CacheKey(
            jobId,
            sessionId,
            verifiedUserMessageId ?? string.Empty,
            jobStartedAt?.ToUniversalTime().Ticks);
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
                    () => ExecuteAndCacheAsync(key, inFlight),
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
        InFlight inFlight)
    {
        // Yield so the caller can publish the in-flight entry before a
        // synchronously completing test executor re-enters the cache lock.
        await Task.Yield();

        OpenCodeOriginalRequestResult result;
        try
        {
            result = await FetchAsync(key, _lifetime.Token).ConfigureAwait(false);
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
                CurrentGeneration(key.JobId) == inFlight.Generation)
            {
                Store(key, result);
            }
        }

        return result;
    }

    private async Task<OpenCodeOriginalRequestResult> FetchAsync(
        CacheKey key,
        CancellationToken lifetimeToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        operationCts.CancelAfter(_options.OperationTimeout);
        var operationToken = operationCts.Token;

        string? cursor = null;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var totalMessages = 0;
        string? singleText = null;
        string? matchedText = null;
        var matchCount = 0;
        UserMessage? nearestMessage = null;
        var nearestDistanceTicks = long.MaxValue;
        var nearestDistanceTied = false;

        for (var page = 0; page < _options.MaxPages; page++)
        {
            try
            {
                operationToken.ThrowIfCancellationRequested();
                var startInfo = CreateStartInfo(key.SessionId, cursor);
                var execution = await _executor
                    .ExecuteAsync(startInfo, operationToken)
                    .WaitAsync(operationToken)
                    .ConfigureAwait(false);

                if (execution.TimedOut)
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.TimedOut);
                }

                if (execution.ReadFailed)
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.ProcessFailure);
                }

                if (execution.OutputLimitExceeded ||
                    execution.StandardOutput.Length > _options.MaxResponseCharacters)
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.ResponseLimitExceeded);
                }

                if (execution.FailedToStart || execution.ExitCode != 0)
                {
                    return OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.ProcessFailure);
                }

                if (!TryParsePage(execution.StandardOutput, out var parsed, out var parseReason))
                {
                    return OpenCodeOriginalRequestResult.Unavailable(parseReason);
                }

                // Validate pagination before interpreting page content. A
                // repeated cursor or an exceeded page bound is an uncertain
                // timeline even when the current page already proves ambiguity.
                if (parsed.Next is not null)
                {
                    if (!seenCursors.Add(parsed.Next))
                    {
                        return OpenCodeOriginalRequestResult.Unavailable(
                            OriginalRequestUnavailableReason.PaginationInconsistent);
                    }

                    if (page == _options.MaxPages - 1)
                    {
                        return OpenCodeOriginalRequestResult.Unavailable(
                            OriginalRequestUnavailableReason.PaginationLimit);
                    }
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
                    else
                    {
                        if (totalMessages == 1)
                        {
                            singleText = message.Text;
                        }

                        if (key.StartTicks is { } startedTicks &&
                            IsSupportedJobStartTicks(startedTicks))
                        {
                            var distanceTicks = Math.Abs(
                                message.Created.UtcTicks - startedTicks);
                            if (distanceTicks < nearestDistanceTicks)
                            {
                                nearestDistanceTicks = distanceTicks;
                                nearestMessage = message;
                                nearestDistanceTied = false;
                            }
                            else if (distanceTicks == nearestDistanceTicks)
                            {
                                nearestDistanceTied = true;
                            }
                        }
                    }
                }

                if (parsed.Next is null)
                {
                    return Resolve(
                        key,
                        totalMessages,
                        singleText,
                        matchCount,
                        matchedText,
                        nearestMessage,
                        nearestDistanceTied,
                        _options.MaxTextCharacters);
                }

                cursor = parsed.Next;
            }
            catch (OperationCanceledException)
            {
                if (lifetimeToken.IsCancellationRequested)
                {
                    throw;
                }

                return OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.TimedOut);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.ProcessFailure);
            }
        }

        return OpenCodeOriginalRequestResult.Unavailable(
            OriginalRequestUnavailableReason.PaginationLimit);
    }

    private static OpenCodeOriginalRequestResult Resolve(
        CacheKey key,
        int totalMessages,
        string? singleText,
        int matchCount,
        string? matchedText,
        UserMessage? nearestMessage,
        bool nearestDistanceTied,
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

        if (totalMessages == 1)
        {
            if (singleText is null)
            {
                return OpenCodeOriginalRequestResult.Ambiguous();
            }

            return singleText.Length > maxTextCharacters
                ? OpenCodeOriginalRequestResult.Unavailable(
                    OriginalRequestUnavailableReason.TextLimitExceeded)
                : OpenCodeOriginalRequestResult.BestEffortSingleton(singleText);
        }

        // Multiple messages can only be attributed by the caller's explicit
        // verified id or, absent one, the uniquely nearest timestamp. A missing
        // start, a tie, or an unusable start leaves the field empty.
        if (key.StartTicks is not { } startedTicks)
        {
            return OpenCodeOriginalRequestResult.Ambiguous();
        }

        if (!IsSupportedJobStartTicks(startedTicks))
        {
            return OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.InvalidJobTimestamp);
        }

        if (nearestDistanceTied || nearestMessage is not { } nearest)
        {
            return OpenCodeOriginalRequestResult.Ambiguous();
        }

        return nearest.Text.Length > maxTextCharacters
            ? OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.TextLimitExceeded)
            : OpenCodeOriginalRequestResult.BestEffortTimeCorrelation(nearest.Text);
    }

    private static bool IsSupportedJobStartTicks(long utcTicks) =>
        utcTicks > 0 && utcTicks < DateTimeOffset.MaxValue.Ticks;

    private ProcessStartInfo CreateStartInfo(string sessionId, string? cursor)
    {
        var path = "/api/session/" +
                   Uri.EscapeDataString(sessionId) +
                   "/message?type=user&limit=" +
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
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("cursor", out var cursor) ||
                cursor.ValueKind != JsonValueKind.Object ||
                (cursor.TryGetProperty("previous", out var previous) &&
                 previous.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) ||
                (cursor.TryGetProperty("next", out var nextElement) &&
                 nextElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)))
            {
                return false;
            }

            var next = nextElement.ValueKind == JsonValueKind.String
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

    private static bool TryParseUserMessage(JsonElement item, out UserMessage message)
    {
        message = default;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !OpenCodeIdentifiers.IsSafeMessageId(idElement.GetString()) ||
            !item.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "user", StringComparison.Ordinal) ||
            !item.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("time", out var timeElement) ||
            timeElement.ValueKind != JsonValueKind.Object ||
            !timeElement.TryGetProperty("created", out var createdElement) ||
            !ExactUnixMillisecondTimestamp.TryParse(createdElement, out var createdTimestamp))
        {
            return false;
        }

        if (item.TryGetProperty("files", out var files) &&
            files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

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
        DateTimeOffset Created);

    private readonly record struct ParsedPage(
        IReadOnlyList<UserMessage> Messages,
        string? Next);
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
