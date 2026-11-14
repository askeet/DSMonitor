namespace DSMonitor.Models;

/// <summary>
/// Exact activity class of one history entry. The discriminator is assigned by
/// the guarded reader from the record's own part type; it is never inferred
/// from display text by presentation code. <see cref="Unknown"/> exists only
/// for synthetic/legacy entries that predate the discriminator.
/// </summary>
public enum LiveActivityKind
{
    Unknown = 0,
    Text,
    Tool,
    StepStart,
    StepFinish
}

/// <summary>
/// Exact tool outcome taken from the record's single <c>part.state.status</c>
/// value. Only the exact token <c>completed</c> maps to
/// <see cref="Completed"/> and only the exact token <c>error</c> maps to
/// <see cref="Failed"/>. Tokens such as <c>complete</c>, <c>done</c>,
/// <c>failed</c>, and <c>failure</c> keep their exact observed display value
/// and are never counted as success or error; running, pending, missing, or
/// unrecognized statuses are retained as their own observed value.
/// </summary>
public enum LiveActivityToolOutcome
{
    Unknown = 0,
    NotTool,
    Running,
    Pending,
    Completed,
    Failed
}

/// <summary>
/// Pairing state of one step row. The projection is derived only after the
/// bounded history window and continuity proof have been applied, so a state
/// is a conservative reading of the retained records, never an inference of
/// job completion.
/// </summary>
public enum LiveActivityStepState
{
    NotStep = 0,
    Paired,
    UnmatchedStart,
    UnmatchedFinish,
    Ambiguous
}

/// <summary>
/// One source-ordered step pairing decision. <see cref="Entry"/> is the
/// retained record that owns the row identity (the start record for a pair,
/// the finish record for an unmatched finish), and <see cref="Finish"/> is the
/// exact matched finish record when the state is <see cref="LiveActivityStepState.Paired"/>.
/// Both records keep their own offsets, identities, digests and timestamps as
/// provenance; nothing is persisted.
/// </summary>
public sealed record LiveActivityStepPair(
    LiveActivityHistoryEntry Entry,
    LiveActivityHistoryEntry? Finish,
    LiveActivityStepState State)
{
    public static LiveActivityStepPair Single(
        LiveActivityHistoryEntry entry,
        LiveActivityStepState state) => new(entry, null, state);
}

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

    /// <summary>
    /// Independently validated tool <c>state.time.start</c> in Unix
    /// milliseconds, reported even when the matching end is missing or invalid.
    /// It is used only as the tool row's Started evidence.
    /// </summary>
    public long? ToolStartUnixMilliseconds { get; init; }

    /// <summary>Exact activity class of this record.</summary>
    public LiveActivityKind ActivityKind { get; init; }

    /// <summary>Exact tool outcome from <c>part.state.status</c>, when this is a tool record.</summary>
    public LiveActivityToolOutcome ToolOutcome { get; init; }

    /// <summary>Bounded, sanitized tool name for tool rows; never command arguments or output.</summary>
    public string? ToolName { get; init; }

    /// <summary>Bounded, sanitized exact <c>state.status</c> token for tool rows.</summary>
    public string? ToolStatus { get; init; }

    /// <summary>Bounded, sanitized step-finish <c>reason</c>; never free text.</summary>
    public string? StepFinishReason { get; init; }

    /// <summary>
    /// True when this step record shares its exact session/message/part
    /// identity with another step record in the same bounded read, so newest
    /// record de-duplication can no longer prove which record is authoritative.
    /// The projection treats such a record as ambiguous and never pairs it.
    /// </summary>
    public bool StepIdentityConflict { get; init; }

    public bool HasKnownSourceTimestamp => SourceTimestamp is not null;
}
