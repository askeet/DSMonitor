namespace DSMonitor.Models;

/// <summary>
/// One job-specific observed message/part in the transient Live Activity
/// history. Identity is exact ordinal session/message/part inside one job and
/// source generation. The entry is never persisted and never establishes
/// execution liveness, completion, success, independent QA, or user
/// acceptance. Text is bounded/sanitized; the complete bounded value is kept
/// for the tab's full-text tooltip.
/// </summary>
public sealed record LiveActivityHistoryEntry(
    string JobId,
    string SessionId,
    string MessageId,
    string PartId,
    string Kind,
    string Text,
    string SourcePath,
    long ByteOffset,
    DateTimeOffset? SourceTimestamp,
    DateTimeOffset ObservedAt,
    string RecordDigest,
    TimeSpan? ObservedDuration)
{
    /// <summary>
    /// Tool-reported <c>state.time.start</c> in Unix milliseconds, when that
    /// exact same-part record supplied a valid start/end pair. This is the only
    /// duration evidence used for the history table; it is never a job duration.
    /// </summary>
    public long? DurationStartUnixMilliseconds { get; init; }

    /// <summary>Tool-reported <c>state.time.end</c> paired with the start above.</summary>
    public long? DurationEndUnixMilliseconds { get; init; }

    public bool HasKnownSourceTimestamp => SourceTimestamp is not null;
}
