using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;

namespace DSMonitor.Services;

/// <summary>Preflight ownership evidence for one metadata-bound source.</summary>
public sealed record LiveActivitySourceEvidence(
    string JobId,
    string? NormalizedPath,
    string? VerifiedPath,
    LedgerSourceIdentity PhysicalIdentity,
    IReadOnlyList<string> DuplicateOwners)
{
    /// <summary>
    /// Guarded-open failure observed during preflight. It lets callers classify
    /// an unavailable source as unsafe (link/reparse/path) rather than merely
    /// missing, without changing which sources are considered available.
    /// </summary>
    public SafeReadFailure Failure { get; init; }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(NormalizedPath) &&
        !string.IsNullOrWhiteSpace(VerifiedPath) &&
        PhysicalIdentity.IsVerified;
}

/// <summary>
/// Bounded, read-only projection of the metadata-bound Forge capture. It never
/// queries OpenCode stores or joins jobs by session, timestamps or directory.
/// A successful ledger observation is required; there is no direct-path read
/// fallback. Recomputing each scan avoids retaining stale content after
/// disappearance, replacement, truncation, or a change in ownership.
/// </summary>
public static class JobLiveActivityReader
{
    public const int MaxReadBytes = 64 * 1024;
    public const int MaxFullReportBytes = 256 * 1024;
    public const int MaxExcerptLength = 600;
    public const int MaxHistoryEntries = 64;
    public const int MaxHistoryTextLength = 4_000;
    private const string AssistantTextKind = "Assistant-reported text";
    private const string RolelessTextKind = "Observed roleless text — unverified";
    private const string AssistantToolKind = "Observed tool activity";
    private const string RolelessToolKind = "Observed roleless tool activity — unverified";
    private const string AssistantResultKind = "Agent-reported result — unverified";
    private const string RolelessResultKind = "Observed roleless result — unverified";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedRootTypes = new(StringComparer.Ordinal)
    {
        "message-part", "step-finish", "step-start", "text", "tool", "tool-use"
    };
    private static readonly HashSet<string> SupportedPartTypes = new(StringComparer.Ordinal)
    {
        "step-finish", "step-start", "text", "tool", "tool-use"
    };

    /// <summary>
    /// Resolve opened source associations once per scan. Evidence includes the
    /// physical identity read from the opened handle and every other job that
    /// claims the same normalized path, verified path, or physical identity.
    /// </summary>
    public static IReadOnlyDictionary<string, LiveActivitySourceEvidence> Preflight(
        IReadOnlyList<ForgeJobMetadata> jobs,
        string runsRoot,
        CancellationToken cancellationToken = default)
    {
        var associations = new List<Association>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? normalizedPath = null;
            string? verifiedPath = null;
            var identity = default(LedgerSourceIdentity);
            var openFailure = SafeReadFailure.None;
            if (job.HasExplicitLogPath && job.LogPath is { } logPath)
            {
                try
                {
                    normalizedPath = Path.GetFullPath(logPath);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    normalizedPath = null;
                }

                if (normalizedPath is not null)
                {
                    using var opened = SafeFileAccess.OpenRead(
                        logPath, runsRoot, rejectHardLinks: true);
                    openFailure = opened.Failure;
                    if (opened.Stream is not null &&
                        opened.VerifiedPath is not null &&
                        opened.PhysicalIdentity.IsVerified)
                    {
                        verifiedPath = opened.VerifiedPath;
                        identity = opened.PhysicalIdentity;
                    }
                }
            }

            associations.Add(new Association(job, normalizedPath, verifiedPath, identity, openFailure));
        }

        var result = new Dictionary<string, LiveActivitySourceEvidence>(StringComparer.Ordinal);
        foreach (var association in associations)
        {
            var owners = associations
                .Where(other => !string.Equals(
                    other.Job.JobId, association.Job.JobId, StringComparison.Ordinal))
                .Where(other => SameAssociation(association, other))
                .Select(other => other.Job.JobId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result[association.Job.JobId] = new LiveActivitySourceEvidence(
                association.Job.JobId,
                association.NormalizedPath,
                association.VerifiedPath,
                association.Identity,
                owners)
            {
                Failure = association.Failure
            };
        }

        return result;
    }

    public static LiveActivitySnapshot Read(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default,
        long maxBytes = MaxReadBytes) =>
        Read(
            job,
            evidence,
            observation,
            runsRoot,
            observedAt,
            cancellationToken,
            maxBytes,
            beforeContentRead: null,
            beforePublishCheck: null);

    internal static LiveActivitySnapshot Read(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        long maxBytes,
        Action? beforeContentRead,
        Action? beforePublishCheck)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(observation);

        var guard = TryOpenGuardedSource(
            job, evidence, observation, runsRoot, out var guarded, out var guardReason);
        if (guard != SourceGuardFailure.None)
        {
            return Unavailable(guardReason);
        }

        using var guardedSource = guarded;
        var stream = guardedSource!.Stream;
        var verifiedPath = guardedSource.VerifiedPath;

        try
        {
            if (stream.Length < observation.CapturedLength)
            {
                return Unavailable(
                    "N/A — source length is below the captured boundary; no excerpt is shown.");
            }

            var count = (int)Math.Min(
                observation.CapturedLength,
                Math.Clamp(maxBytes, 1, MaxReadBytes));
            var start = observation.CapturedLength - count;
            if (!TryReadWindow(
                    stream,
                    start,
                    count,
                    beforeContentRead,
                    cancellationToken,
                    out var bytes,
                    out var read))
            {
                return Unavailable(
                    "N/A — the bounded capture could not be read completely; no excerpt is shown.");
            }

            ActivityExcerpt? doing = null;
            ActivityExcerpt? done = null;
            ActivityExcerpt? latestText = null;
            var texts = new Dictionary<(string Session, string Message), (ActivityExcerpt Excerpt, bool VerifiedAssistant)>();
            var history = new Dictionary<(string Session, string Message, string PartId), LiveActivityHistoryEntry>();
            // Discard the first record when starting mid-file, even if it
            // happens to be complete. Never parse a possibly partial record.
            var lineStart = start == 0 ? 0 : Array.IndexOf(bytes, (byte)'\n', 0, read) + 1;
            if (start > 0 && lineStart == 0)
            {
                return Unavailable("N/A — no complete record in bounded source window.");
            }

            for (var index = lineStart; index < read; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                cancellationToken.ThrowIfCancellationRequested();
                var offset = start + lineStart;
                var recordStart = lineStart;
                lineStart = index + 1;
                try
                {
                    var line = StrictUtf8.GetString(bytes, recordStart, index - recordStart)
                        .TrimStart('\uFEFF');
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !TryGetSingleObject(root, "part", out var part))
                    {
                        continue;
                    }

                    if (!TryResolvePreviewIdentity(
                            root, part, out var session, out var message, out var partId))
                    {
                        continue;
                    }

                    if (!TryGetRoleEvidence(root, part, out var verifiedAssistant)) continue;
                    if (!TryGetActivityKind(root, part, out var isText, out var isTool, out var isFinish))
                    {
                        continue;
                    }

                    if (!ForgeEventParser.TryParse(line, job.JobId, observedAt, out var parsed) ||
                        parsed is null)
                    {
                        continue;
                    }

                    var key = (session!, message!);
                    var sourcePath = verifiedPath;
                    var recordDigest = ComputeDigest(bytes, recordStart, index - recordStart);
                    ActivityExcerpt Excerpt(string text, string kind) => new(
                        Sanitize(text),
                        kind,
                        job.JobId,
                        session!,
                        message!,
                        ValidId(partId) ? partId : null,
                        sourcePath,
                        offset,
                        parsed.Event.Timestamp,
                        observedAt)
                    {
                        RecordDigest = recordDigest
                    };
                    LiveActivityHistoryEntry? HistoryEntry(
                        string text,
                        string kind,
                        bool toolRecord)
                    {
                        // History identity must be exact and displayable; a
                        // missing/unsafe part id cannot be de-duplicated.
                        if (!ValidId(partId))
                        {
                            return null;
                        }

                        TimeSpan? duration = null;
                        long? durationStart = null;
                        long? durationEnd = null;
                        if (toolRecord &&
                            TryGetToolDuration(
                                part,
                                out var observedDuration,
                                out var start,
                                out var end))
                        {
                            duration = observedDuration;
                            durationStart = start;
                            durationEnd = end;
                        }

                        return new LiveActivityHistoryEntry(
                            job.JobId,
                            session!,
                            message!,
                            partId!,
                            kind,
                            Sanitize(text, MaxHistoryTextLength),
                            sourcePath,
                            offset,
                            parsed.Event.Timestamp,
                            observedAt,
                            recordDigest,
                            duration)
                        {
                            DurationStartUnixMilliseconds = durationStart,
                            DurationEndUnixMilliseconds = durationEnd
                        };
                    }
                    void RememberHistory(LiveActivityHistoryEntry? entry)
                    {
                        if (entry is null)
                        {
                            return;
                        }

                        var historyKey = (entry.SessionId, entry.MessageId, entry.PartId);
                        if (!history.TryGetValue(historyKey, out var existing) ||
                            entry.ByteOffset >= existing.ByteOffset)
                        {
                            history[historyKey] = entry;
                        }
                    }
                    if (isText &&
                        TryGetSingleString(part, "text", out var text, out var hasText) &&
                        hasText &&
                        !string.IsNullOrWhiteSpace(text))
                    {
                        doing = Excerpt(
                            text,
                            verifiedAssistant ? AssistantTextKind : RolelessTextKind);
                        latestText = doing;
                        texts[key] = (doing, verifiedAssistant);
                        RememberHistory(HistoryEntry(
                            text,
                            verifiedAssistant ? AssistantTextKind : RolelessTextKind,
                            toolRecord: false));
                    }
                    else if (isTool)
                    {
                        // The existing parser emits safe tool names/states,
                        // excluding command arguments, output and secrets.
                        doing = Excerpt(
                            parsed.Event.Summary,
                            verifiedAssistant ? AssistantToolKind : RolelessToolKind);
                        RememberHistory(HistoryEntry(
                            parsed.Event.Summary,
                            verifiedAssistant ? AssistantToolKind : RolelessToolKind,
                            toolRecord: true));
                    }
                    else if (isFinish &&
                             TryGetSingleString(part, "reason", out var finishReason, out var hasReason) &&
                             hasReason &&
                             finishReason is "stop" or "end-turn" &&
                             texts.TryGetValue(key, out var result))
                    {
                        var verifiedResult = verifiedAssistant && result.VerifiedAssistant;
                        done = result.Excerpt with
                        {
                            Kind = verifiedResult ? AssistantResultKind : RolelessResultKind
                        };
                    }
                }
                catch (JsonException) { /* Malformed/overlong/partial records are unavailable. */ }
                catch (DecoderFallbackException) { /* Invalid UTF-8 cannot establish source identity. */ }
                catch (InvalidOperationException) { /* Invalid escaped text/identity cannot be attributed. */ }
            }

            beforePublishCheck?.Invoke();
            if (!SourceStillMatches(
                    job,
                    runsRoot,
                    observation,
                    verifiedPath,
                    stream,
                    out var reason))
            {
                return Unavailable(reason);
            }

            return new LiveActivitySnapshot
            {
                Doing = doing,
                Done = done,
                LatestText = latestText,
                History = LiveActivityHistoryPolicy.Order(history.Values),
                ReadWindowStart = start,
                ReadWindowEnd = start + read,
                Availability = "Bounded source re-read; timestamps describe source records, not execution liveness."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unavailable("N/A — job source changed or became unavailable during read.");
        }
    }

    /// <summary>
    /// Selects the newest eligible text excerpt by physical source order that
    /// may carry an explicit reported status. The latest text is retained even
    /// after later tool activity, so a stale finished result cannot win merely
    /// because a new finish event has not arrived. If the newest text lacks a
    /// part identity or record binding, the result is null rather than falling
    /// back to an older report.
    /// </summary>
    public static FullReportTarget? SelectReportedStatusTarget(LiveActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var newestText = SelectNewest(
            activity.LatestText,
            IsTextExcerpt(activity.Doing) ? activity.Doing : null);
        var excerpt = SelectNewest(newestText, activity.Done);
        if (excerpt is null ||
            string.IsNullOrWhiteSpace(excerpt.PartId) ||
            string.IsNullOrWhiteSpace(excerpt.RecordDigest))
        {
            return null;
        }

        return FullReportTarget.FromExcerpt(excerpt);
    }

    private static ActivityExcerpt? SelectNewest(
        ActivityExcerpt? first,
        ActivityExcerpt? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first.ByteOffset >= second.ByteOffset ? first : second;
    }

    /// <summary>
    /// Reuses the guarded job-specific full-report retrieval and the pure
    /// reported-status parser for one eligible text excerpt. Returns null when
    /// no eligible text target is available. An ineligible, incomplete,
    /// unavailable, or unsafe report still yields a non-null extraction that the
    /// presentation policy maps to N/A; no other part is substituted.
    /// </summary>
    public static ReportedStatusExtraction? ReadReportedStatus(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        LiveActivitySnapshot activity,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        var target = SelectReportedStatusTarget(activity);
        if (target is null)
        {
            return null;
        }

        var report = ReadFullReport(
            job,
            evidence,
            observation,
            target,
            runsRoot,
            observedAt,
            cancellationToken);
        return ReportedStatusParser.Extract(report);
    }

    private static bool IsTextExcerpt(ActivityExcerpt? excerpt) =>
        excerpt is not null &&
        excerpt.Kind is AssistantTextKind or RolelessTextKind or
            AssistantResultKind or RolelessResultKind;

    /// <summary>
    /// Retrieve one previously selected text part from the same guarded,
    /// metadata-bound source used by the preview. The bounded cap is
    /// <see cref="MaxFullReportBytes"/>; the 600-character preview and its
    /// selection behavior are unchanged. The result is transient and never
    /// persisted. A missing, incomplete, or unverified target is never replaced
    /// by another part, and no multipart assembly is performed. A target that
    /// carries the preview record digest binds the exact record bytes, so
    /// same-length in-place rewrites are detected; a target without that binding
    /// fails closed instead of returning unproven content.
    /// </summary>
    public static FullReportResult ReadFullReport(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        FullReportTarget target,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default) =>
        ReadFullReport(
            job,
            evidence,
            observation,
            target,
            runsRoot,
            observedAt,
            cancellationToken,
            beforeContentRead: null,
            beforePublishCheck: null);

    internal static FullReportResult ReadFullReport(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        FullReportTarget target,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        Action? beforeContentRead,
        Action? beforePublishCheck)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(target);

        FullReportResult Fail(FullReportStatus status, string reason) =>
            EmptyResult(status, reason, job.JobId, target, observedAt);

        if (!string.Equals(target.JobId, job.JobId, StringComparison.Ordinal))
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — the target belongs to a different job; no report is shown.");
        }

        if (target.PartId is null || string.IsNullOrWhiteSpace(target.PartId))
        {
            return Fail(
                FullReportStatus.Unavailable,
                "N/A — the target part identity is unavailable; no substitute part is shown.");
        }

        if (!ValidId(target.SessionId) || !ValidId(target.MessageId) || !ValidId(target.PartId))
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — the target identity is unsafe; no report is shown.");
        }

        var guard = TryOpenGuardedSource(
            job, evidence, observation, runsRoot, out var guarded, out var guardReason);
        if (guard != SourceGuardFailure.None)
        {
            return Fail(
                guard == SourceGuardFailure.Unsafe
                    ? FullReportStatus.Unsafe
                    : FullReportStatus.Unavailable,
                guardReason);
        }

        using var guardedSource = guarded;
        var stream = guardedSource!.Stream;
        var verifiedPath = guardedSource.VerifiedPath;
        try
        {
            if (stream.Length < observation.CapturedLength)
            {
                return Fail(
                    FullReportStatus.Incomplete,
                    "N/A — source length is below the captured boundary; full content is not available.");
            }

            if (observation.CapturedLength <= 0)
            {
                return Fail(
                    FullReportStatus.Unavailable,
                    "N/A — the bounded source capture is empty; no report is shown.");
            }

            var count = (int)Math.Min(
                observation.CapturedLength,
                (long)MaxFullReportBytes);
            var start = observation.CapturedLength - count;
            if (!TryReadWindow(
                    stream,
                    start,
                    count,
                    beforeContentRead,
                    cancellationToken,
                    out var bytes,
                    out var read))
            {
                return Fail(
                    FullReportStatus.Incomplete,
                    "N/A — the bounded capture could not be read completely; full content is not available.");
            }

            // The bounded read is exactly min(captured length, cap). A target at
            // the start of a non-zero window cannot be proven to begin a record
            // without reading a 262145th predecessor byte, so it fails closed.
            if (!TryLocateTargetRecord(
                    bytes,
                    read,
                    start,
                    target,
                    out var recordStart,
                    out var recordEnd,
                    out var locateStatus,
                    out var locateReason))
            {
                return Fail(locateStatus, locateReason);
            }

            var result = ReadTargetRecord(
                bytes,
                recordStart,
                recordEnd,
                start,
                target,
                job,
                verifiedPath,
                observedAt);
            if (!result.IsComplete)
            {
                return result;
            }

            if (string.IsNullOrWhiteSpace(target.ExpectedRecordDigest))
            {
                return Fail(
                    FullReportStatus.Incomplete,
                    "N/A — the target has no exact source-record binding; full content cannot be proven.");
            }

            if (!IsHexDigest(target.ExpectedRecordDigest))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record binding is invalid; no report is shown.");
            }

            var actualDigest = ComputeDigest(bytes, recordStart, recordEnd - recordStart);
            if (!string.Equals(
                    actualDigest,
                    target.ExpectedRecordDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the selected source record changed since preview; no substitute content is shown.");
            }

            beforePublishCheck?.Invoke();
            if (!SourceStillMatches(
                    job,
                    runsRoot,
                    observation,
                    verifiedPath,
                    stream,
                    out _))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — source changed before publication; the full report was discarded.");
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — job source changed or became unavailable during read.");
        }
    }

    private static bool TryLocateTargetRecord(
        byte[] bytes,
        int read,
        long windowStart,
        FullReportTarget target,
        out int recordStart,
        out int recordEnd,
        out FullReportStatus status,
        out string reason)
    {
        recordStart = -1;
        recordEnd = -1;
        status = FullReportStatus.Unavailable;
        reason = "N/A — the target part is not present in the bounded capture.";

        if (target.ExpectedByteOffset is { } expected)
        {
            if (expected < 0)
            {
                status = FullReportStatus.Unsafe;
                reason = "N/A — the target offset is invalid; no report is shown.";
                return false;
            }

            if (expected < windowStart)
            {
                status = FullReportStatus.Incomplete;
                reason = "N/A — the target record begins before the bounded window; full content is not available.";
                return false;
            }

            var relative = expected - windowStart;
            if (relative >= read)
            {
                status = FullReportStatus.Incomplete;
                reason = "N/A — the target record is outside the captured boundary; full content is not available.";
                return false;
            }

            var start = (int)relative;
            if (start == 0 && windowStart > 0)
            {
                status = FullReportStatus.Incomplete;
                reason =
                    "N/A — the bounded window start cannot be proven to be a record boundary within the source-byte cap; full content is not available.";
                return false;
            }

            if (start > 0 && bytes[start - 1] != (byte)'\n')
            {
                status = FullReportStatus.Unsafe;
                reason = "N/A — the target record boundary no longer matches the observed offset; no substitute part is shown.";
                return false;
            }

            var end = Array.IndexOf(bytes, (byte)'\n', start, read - start);
            if (end < 0)
            {
                status = FullReportStatus.Incomplete;
                reason = "N/A — the target record is not newline-terminated in the bounded capture; full content is not available.";
                return false;
            }

            recordStart = start;
            recordEnd = end;
            return true;
        }

        // No expected offset: match the exact session/message/part identity and
        // never fall back to a different part. Discard a possibly partial first
        // record when the window starts mid-file.
        var lineStart = windowStart == 0 ? 0 : Array.IndexOf(bytes, (byte)'\n', 0, read) + 1;
        if (windowStart > 0 && lineStart == 0)
        {
            status = FullReportStatus.Incomplete;
            reason = "N/A — the bounded window contains no complete record; full content is not available.";
            return false;
        }

        var matchStart = -1;
        var matchEnd = -1;
        for (var index = lineStart; index < read; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            var candidateStart = lineStart;
            var candidateEnd = index;
            lineStart = index + 1;
            if (!RecordIdentityMatches(bytes, candidateStart, candidateEnd, target)) continue;
            if (matchStart >= 0)
            {
                status = FullReportStatus.Unsafe;
                reason = "N/A — multiple records match the target part; the target is ambiguous.";
                return false;
            }

            matchStart = candidateStart;
            matchEnd = candidateEnd;
        }

        if (matchStart >= 0)
        {
            // Never publish an earlier complete match while a newer
            // unterminated record also matches the target; the tail may be a
            // duplicate or a conflicting partial record.
            if (lineStart < read &&
                RecordIdentityMatches(bytes, lineStart, read, target))
            {
                status = FullReportStatus.Incomplete;
                reason = "N/A — an unterminated record also matches the target; the target is ambiguous or incomplete.";
                return false;
            }

            recordStart = matchStart;
            recordEnd = matchEnd;
            return true;
        }

        if (lineStart < read &&
            RecordIdentityMatches(bytes, lineStart, read, target))
        {
            status = FullReportStatus.Incomplete;
            reason = "N/A — the target record is not newline-terminated in the bounded capture; full content is not available.";
            return false;
        }

        return false;
    }

    private static bool RecordIdentityMatches(
        byte[] bytes,
        int start,
        int end,
        FullReportTarget target)
    {
        try
        {
            var line = StrictUtf8.GetString(bytes, start, end - start).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetSingleObject(root, "part", out var part))
            {
                return false;
            }

            return TryResolveIdentity(root, part, out var session, out var message, out var partId) &&
                   string.Equals(session, target.SessionId, StringComparison.Ordinal) &&
                   string.Equals(message, target.MessageId, StringComparison.Ordinal) &&
                   string.Equals(partId, target.PartId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static FullReportResult ReadTargetRecord(
        byte[] bytes,
        int recordStart,
        int recordEnd,
        long windowStart,
        FullReportTarget target,
        ForgeJobMetadata job,
        string verifiedPath,
        DateTimeOffset observedAt)
    {
        var byteOffset = windowStart + recordStart;
        var recordLength = (long)(recordEnd - recordStart);
        FullReportResult Fail(FullReportStatus status, string reason) => new(
            status,
            reason,
            null,
            job.JobId,
            target.SessionId,
            target.MessageId,
            target.PartId,
            verifiedPath,
            byteOffset,
            recordLength,
            null,
            observedAt,
            string.Empty);

        string line;
        try
        {
            line = StrictUtf8.GetString(bytes, recordStart, recordEnd - recordStart)
                .TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — the target record contains invalid UTF-8; no report is shown.");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetSingleObject(root, "part", out var part))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record does not contain exactly one part object.");
            }

            if (!TryResolveIdentity(root, part, out var session, out var message, out var partId) ||
                !string.Equals(session, target.SessionId, StringComparison.Ordinal) ||
                !string.Equals(message, target.MessageId, StringComparison.Ordinal) ||
                !string.Equals(partId, target.PartId, StringComparison.Ordinal))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the record no longer matches the targeted part; no substitute part is shown.");
            }

            if (!TryGetRoleEvidence(root, part, out var verifiedAssistant))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record has conflicting, unsupported, or user role evidence.");
            }

            if (!TryGetActivityKind(root, part, out var isText, out _, out _) || !isText)
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record is not an eligible text part.");
            }

            if (!TryGetSingleString(part, "text", out var text, out var hasText) ||
                !hasText ||
                string.IsNullOrWhiteSpace(text))
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record has no single eligible text content.");
            }

            if (!ForgeEventParser.TryParse(line, job.JobId, observedAt, out var parsed) ||
                parsed is null)
            {
                return Fail(
                    FullReportStatus.Unsafe,
                    "N/A — the target record could not be parsed as an eligible Forge event.");
            }

            var kind = verifiedAssistant ? AssistantTextKind : RolelessTextKind;
            return new FullReportResult(
                FullReportStatus.Complete,
                "Complete guarded capture of one text part.",
                text,
                job.JobId,
                session!,
                message!,
                partId!,
                verifiedPath,
                byteOffset,
                recordLength,
                parsed.Event.Timestamp,
                observedAt,
                kind);
        }
        catch (JsonException)
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — the target record is malformed JSON; no report is shown.");
        }
        catch (InvalidOperationException)
        {
            return Fail(
                FullReportStatus.Unsafe,
                "N/A — the target record contains malformed escaped text or identity; no report is shown.");
        }
    }

    private static bool TryResolveIdentity(
        JsonElement root,
        JsonElement part,
        out string? session,
        out string? message,
        out string? partId) =>
        TryResolveIdentityCore(
            root, part, requireSafePartId: true, out session, out message, out partId);

    /// <summary>
    /// Preview variant: strict alias and root/part consistency checks, but an
    /// unsafe single part id remains displayable as null rather than rejecting
    /// the whole observation.
    /// </summary>
    private static bool TryResolvePreviewIdentity(
        JsonElement root,
        JsonElement part,
        out string? session,
        out string? message,
        out string? partId) =>
        TryResolveIdentityCore(
            root, part, requireSafePartId: false, out session, out message, out partId);

    private static bool TryResolveIdentityCore(
        JsonElement root,
        JsonElement part,
        bool requireSafePartId,
        out string? session,
        out string? message,
        out string? partId)
    {
        session = null;
        message = null;
        partId = null;

        if (!TryGetAlias(root, "sessionID", "sessionId", out var rootSession) ||
            !TryGetAlias(part, "sessionID", "sessionId", out var partSession) ||
            !TryGetAlias(root, "messageID", "messageId", out var rootMessage) ||
            !TryGetAlias(part, "messageID", "messageId", out var partMessage) ||
            !TryGetAlias(part, "id", "partID", out var partIdentity))
        {
            return false;
        }

        if (rootSession is not null &&
            partSession is not null &&
            !string.Equals(rootSession, partSession, StringComparison.Ordinal))
        {
            return false;
        }

        if (rootMessage is not null &&
            partMessage is not null &&
            !string.Equals(rootMessage, partMessage, StringComparison.Ordinal))
        {
            return false;
        }

        session = rootSession ?? partSession;
        message = partMessage;
        partId = partIdentity;
        return ValidId(session) &&
               ValidId(message) &&
               (!requireSafePartId || ValidId(partId));
    }

    /// <summary>
    /// Resolve one aliased identity field without null-coalescing: duplicate
    /// aliases or conflicting alias values are rejected.
    /// </summary>
    private static bool TryGetAlias(
        JsonElement value,
        string primaryName,
        string alternateName,
        out string? result)
    {
        result = null;
        if (!TryGetSingleString(value, primaryName, out var primary, out var hasPrimary) ||
            !TryGetSingleString(value, alternateName, out var alternate, out var hasAlternate))
        {
            return false;
        }

        if (hasPrimary && hasAlternate)
        {
            // Equal alias forms are consistent; conflicting values are rejected.
            if (!string.Equals(primary, alternate, StringComparison.Ordinal))
            {
                return false;
            }

            result = primary;
            return true;
        }

        result = hasPrimary ? primary : alternate;
        return true;
    }

    private static bool TryGetSingleObject(
        JsonElement value,
        string name,
        out JsonElement result)
    {
        result = default;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind != JsonValueKind.Object) return false;
            result = property.Value;
        }

        return count == 1;
    }

    private static bool TryGetSingleString(
        JsonElement value,
        string name,
        out string? result,
        out bool present)
    {
        result = null;
        present = false;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                present = true;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String) return false;
            result = property.Value.GetString();
            present = true;
        }

        return true;
    }

    private static FullReportResult EmptyResult(
        FullReportStatus status,
        string reason,
        string jobId,
        FullReportTarget target,
        DateTimeOffset observedAt) => new(
            status,
            reason,
            null,
            jobId,
            target.SessionId,
            target.MessageId,
            target.PartId,
            null,
            null,
            null,
            null,
            observedAt,
            string.Empty);

    private static string ComputeDigest(byte[] bytes, int start, int length) =>
        Convert.ToHexString(SHA256.HashData(bytes.AsSpan(start, length)));

    private static bool IsHexDigest(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadWindow(
        FileStream stream,
        long start,
        int count,
        Action? beforeContentRead,
        CancellationToken cancellationToken,
        out byte[] bytes,
        out int read)
    {
        bytes = new byte[count];
        stream.Position = start;
        beforeContentRead?.Invoke();
        read = 0;
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = stream.Read(bytes, read, count - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return read == count;
    }

    private enum SourceGuardFailure
    {
        None,
        Unavailable,
        Unsafe
    }

    private static SourceGuardFailure TryOpenGuardedSource(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        string runsRoot,
        out GuardedSource? source,
        out string reason)
    {
        source = null;

        if (job.IsProvisional || !job.HasExplicitLogPath || job.LogPath is null)
        {
            reason = "N/A — explicit metadata log association unavailable.";
            return SourceGuardFailure.Unavailable;
        }

        if (!TryNormalize(job.LogPath, out var jobPath) ||
            !TryNormalize(evidence.NormalizedPath, out var evidencePath) ||
            !string.Equals(jobPath, evidencePath, StringComparison.OrdinalIgnoreCase))
        {
            reason = "N/A — job/source association could not be verified.";
            return SourceGuardFailure.Unsafe;
        }

        if (!string.Equals(evidence.JobId, job.JobId, StringComparison.Ordinal))
        {
            reason = "N/A — preflight evidence belongs to a different job.";
            return SourceGuardFailure.Unsafe;
        }

        if (evidence.DuplicateOwners is { Count: > 0 })
        {
            reason =
                "N/A — multiple jobs claim this source or its physical identity is unavailable; ownership is ambiguous.";
            return SourceGuardFailure.Unsafe;
        }

        if (!evidence.IsAvailable)
        {
            reason =
                "N/A — multiple jobs claim this source or its physical identity is unavailable; ownership is ambiguous.";
            return evidence.Failure is SafeReadFailure.OutsideRoot or
                SafeReadFailure.ReparsePoint or SafeReadFailure.Unverifiable
                ? SourceGuardFailure.Unsafe
                : SourceGuardFailure.Unavailable;
        }

        if (!string.Equals(observation.JobId, job.JobId, StringComparison.Ordinal) ||
            !TryNormalize(observation.SourcePath, out var observedPath) ||
            !string.Equals(observedPath, evidencePath, StringComparison.OrdinalIgnoreCase))
        {
            reason = "N/A — ingestion observation belongs to a different job or source.";
            return SourceGuardFailure.Unsafe;
        }

        if (!observation.IsConsistent ||
            !observation.Identity.Equals(evidence.PhysicalIdentity))
        {
            reason = "N/A — source physical identity is unavailable, weak, or changed before ingestion.";
            return SourceGuardFailure.Unsafe;
        }

        var opened = SafeFileAccess.OpenRead(
            job.LogPath, runsRoot, rejectHardLinks: true);
        var verifiedPath = opened.VerifiedPath;
        if (opened.Stream is null ||
            verifiedPath is null ||
            !opened.PhysicalIdentity.Equals(observation.Identity) ||
            !string.Equals(
                verifiedPath,
                evidence.VerifiedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            var unsafeFailure = opened.Stream is not null ||
                                opened.Failure is SafeReadFailure.OutsideRoot or
                                    SafeReadFailure.ReparsePoint or SafeReadFailure.Unverifiable;
            opened.Dispose();
            reason = "N/A — job source unavailable or physical identity changed; no excerpt is shown.";
            return unsafeFailure
                ? SourceGuardFailure.Unsafe
                : SourceGuardFailure.Unavailable;
        }

        source = new GuardedSource(opened, verifiedPath);
        reason = string.Empty;
        return SourceGuardFailure.None;
    }

    private sealed class GuardedSource : IDisposable
    {
        public GuardedSource(SafeReadResult opened, string verifiedPath)
        {
            Opened = opened;
            VerifiedPath = verifiedPath;
        }

        private SafeReadResult Opened { get; }
        public FileStream Stream => Opened.Stream!;
        public string VerifiedPath { get; }
        public void Dispose() => Opened.Dispose();
    }

    private static bool SourceStillMatches(
        ForgeJobMetadata job,
        string runsRoot,
        LedgerSourceObservation observation,
        string verifiedPath,
        FileStream stream,
        out string reason)
    {
        reason = "N/A — source changed before publication; all excerpts were discarded.";
        try
        {
            if (stream.Length != observation.CapturedLength)
            {
                return false;
            }

            using var recheck = SafeFileAccess.OpenRead(
                job.LogPath, runsRoot, rejectHardLinks: true);
            if (recheck.Stream is not { } reopened ||
                recheck.VerifiedPath is null ||
                !recheck.PhysicalIdentity.Equals(observation.Identity) ||
                !string.Equals(
                    recheck.VerifiedPath,
                    verifiedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                reopened.Length != observation.CapturedLength)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetAssistantRole(JsonElement root, JsonElement part)
    {
        if (!TryGetSingleRole(root, out var rootRole) ||
            !TryGetSingleRole(part, out var partRole))
        {
            return false;
        }

        if (rootRole is not null &&
            partRole is not null &&
            !string.Equals(rootRole, partRole, StringComparison.Ordinal))
        {
            return false;
        }

        var effective = partRole ?? rootRole;
        return string.Equals(effective, "assistant", StringComparison.Ordinal);
    }

    /// <summary>
    /// Preserve the strict assistant classification and add only the authorized
    /// roleless-observation path. Any explicit, malformed, duplicate,
    /// unsupported or conflicting role remains rejected.
    /// </summary>
    private static bool TryGetRoleEvidence(
        JsonElement root,
        JsonElement part,
        out bool verifiedAssistant)
    {
        verifiedAssistant = TryGetAssistantRole(root, part);
        if (verifiedAssistant)
        {
            return true;
        }

        return IsRolelessOnly(root, part);
    }

    private static bool IsRolelessOnly(JsonElement root, JsonElement part) =>
        TryGetSingleRole(root, out var rootRole) &&
        TryGetSingleRole(part, out var partRole) &&
        rootRole is null &&
        partRole is null;

    private static bool TryGetSingleRole(JsonElement value, out string? role)
    {
        role = null;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals("role")) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Value.ValueKind != JsonValueKind.String) return false;
            role = property.Value.GetString();
        }

        return true;
    }

    private static bool TryGetActivityKind(
        JsonElement root,
        JsonElement part,
        out bool isText,
        out bool isTool,
        out bool isFinish)
    {
        isText = false;
        isTool = false;
        isFinish = false;
        if (!TryGetSingleType(root, out var rootType) ||
            !TryGetSingleType(part, out var partType) ||
            rootType is null ||
            !SupportedRootTypes.Contains(rootType) ||
            (partType is not null && !SupportedPartTypes.Contains(partType)))
        {
            return false;
        }

        var rootCategory = Category(rootType);
        if (rootType == "message-part")
        {
            if (partType is null) return false;
            var wrappedCategory = Category(partType);
            if (wrappedCategory is null) return false;
            isText = wrappedCategory == "text";
            isTool = wrappedCategory == "tool";
            isFinish = wrappedCategory == "finish";
            return true;
        }

        if (rootCategory is null) return false;
        if (partType is not null && !string.Equals(Category(partType), rootCategory, StringComparison.Ordinal))
        {
            return false;
        }

        isText = rootCategory == "text";
        isTool = rootCategory == "tool";
        isFinish = rootCategory == "finish";
        return true;
    }

    private static bool TryGetSingleType(JsonElement value, out string? type)
    {
        type = null;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals("type")) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind == JsonValueKind.Null) return false;
            if (property.Value.ValueKind != JsonValueKind.String) return false;
            type = NormalizeType(property.Value.GetString());
        }

        return true;
    }

    private static string? Category(string type) => type switch
    {
        "text" => "text",
        "tool" or "tool-use" => "tool",
        "step-finish" => "finish",
        _ => null
    };

    private static string NormalizeType(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('_', '-').ToLowerInvariant();

    private static bool SameAssociation(Association left, Association right)
    {
        var samePath = left.NormalizedPath is not null &&
                       right.NormalizedPath is not null &&
                       string.Equals(
                           left.NormalizedPath,
                           right.NormalizedPath,
                           StringComparison.OrdinalIgnoreCase);
        var sameVerified = left.VerifiedPath is not null &&
                           right.VerifiedPath is not null &&
                           string.Equals(
                               left.VerifiedPath,
                               right.VerifiedPath,
                               StringComparison.OrdinalIgnoreCase);
        var sameIdentity = left.Identity.IsVerified &&
                           right.Identity.IsVerified &&
                           left.Identity.Equals(right.Identity);
        return samePath || sameVerified || sameIdentity;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static LiveActivitySnapshot Unavailable(string reason) => new() { Availability = reason };
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 && !value.Any(character => char.IsControl(character) ||
            char.GetUnicodeCategory(character) == UnicodeCategory.Format);

    private static bool TryGetToolDuration(
        JsonElement part,
        out TimeSpan duration,
        out long startMilliseconds,
        out long endMilliseconds)
    {
        duration = default;
        startMilliseconds = 0;
        endMilliseconds = 0;
        if (!TryGetSingleObject(part, "state", out var state) ||
            !TryGetSingleObject(state, "time", out var time) ||
            !TryGetInt64(time, "start", out startMilliseconds) ||
            !TryGetInt64(time, "end", out endMilliseconds))
        {
            return false;
        }

        if (startMilliseconds < 0 ||
            endMilliseconds < startMilliseconds)
        {
            return false;
        }

        var delta = (double)endMilliseconds - startMilliseconds;
        if (delta < 0 || delta > TimeSpan.MaxValue.TotalMilliseconds)
        {
            return false;
        }

        duration = TimeSpan.FromMilliseconds(delta);
        return true;
    }

    private static bool TryGetInt64(JsonElement value, string name, out long result)
    {
        result = 0;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind != JsonValueKind.Number) return false;
            if (!property.Value.TryGetInt64(out result)) return false;
        }

        return count == 1;
    }

    public static string Sanitize(string value) =>
        Sanitize(value, MaxExcerptLength);

    private static string Sanitize(string value, int maxLength)
    {
        var builder = new StringBuilder(maxLength);
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                if (rune.Value is '\n' or '\r' or '\t') builder.Append(' ');
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > maxLength) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private sealed record Association(
        ForgeJobMetadata Job,
        string? NormalizedPath,
        string? VerifiedPath,
        LedgerSourceIdentity Identity,
        SafeReadFailure Failure);
}
