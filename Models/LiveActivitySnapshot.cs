namespace DSMonitor.Models;

/// <summary>Transient content: never passed to the ledger or monitoring logger.</summary>
public sealed record ActivityExcerpt(
    string Text,
    string Kind,
    string JobId,
    string SessionId,
    string MessageId,
    string? PartId,
    string SourcePath,
    long ByteOffset,
    DateTimeOffset? SourceTimestamp,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// SHA-256 of the exact raw record bytes observed for this excerpt. It binds
    /// a later full-report request to the same source record and detects
    /// length-changing or same-length content rewrites. Transient only.
    /// </summary>
    public string? RecordDigest { get; init; }
}

public sealed record LiveActivitySnapshot
{
    public ActivityExcerpt? Doing { get; init; }
    public ActivityExcerpt? Done { get; init; }

    /// <summary>
    /// Latest eligible text excerpt in physical source order, retained even
    /// when a later tool activity replaces <see cref="Doing"/>. Reported-status
    /// selection uses this to avoid preferring an older <see cref="Done"/>
    /// result after newer text arrives. Transient; never persisted.
    /// </summary>
    internal ActivityExcerpt? LatestText { get; init; }

    /// <summary>
    /// Current bounded, job-specific history window observed by the guarded
    /// read. The monitoring service merges this with prior verified history;
    /// this property is already de-duplicated by exact session/message/part
    /// identity and ordered newest source timestamp first (unknown timestamps
    /// last, newer byte offset as tie-break). Transient only.
    /// </summary>
    public IReadOnlyList<LiveActivityHistoryEntry> History { get; init; } =
        Array.Empty<LiveActivityHistoryEntry>();

    /// <summary>
    /// Every history-eligible record parsed from this bounded read before
    /// de-duplication by exact session/message/part identity. The monitoring
    /// service uses these exact-offset records only to prove that previously
    /// retained history still matches the source byte-for-byte; they are never
    /// displayed directly. Transient only.
    /// </summary>
    internal IReadOnlyList<LiveActivityHistoryEntry> HistoryRecords { get; init; } =
        Array.Empty<LiveActivityHistoryEntry>();

    /// <summary>
    /// Absolute first byte of the successfully parsed bounded window. Together
    /// with <see cref="ReadWindowEnd"/> this proves which previously complete
    /// records are still visible when deciding whether history may be carried
    /// across a refresh. A negative end means no successful window was parsed.
    /// </summary>
    internal long ReadWindowStart { get; init; } = -1;

    /// <summary>Exclusive absolute end of the successfully parsed bounded window.</summary>
    internal long ReadWindowEnd { get; init; } = -1;

    internal bool HasReadWindow => ReadWindowEnd >= 0 && ReadWindowStart >= 0;

    /// <summary>
    /// Absolute source offset of the exact step-finish record that produced
    /// <see cref="Done"/>, when one was parsed in the current bounded read. It
    /// lets display policy compare terminal evidence with later activity by
    /// physical source order instead of by the text record the result describes.
    /// Transient only.
    /// </summary>
    internal long? DoneEventByteOffset { get; init; }

    /// <summary>
    /// Absolute source offset of the newest text, tool, step-start or
    /// step-finish record parsed in this bounded read. It lets spinner policy
    /// recognize a new task after a terminal event even before a Doing excerpt
    /// is available. Transient only.
    /// </summary>
    internal long? LatestLifecycleByteOffset { get; init; }

    /// <summary>
    /// Optional transient callback that retrieves the complete guarded text for
    /// one selected target using the same job/source evidence as this read.
    /// It performs no persistence, uses no new reader, and is null when the
    /// current snapshot has no safely attributable source context.
    /// </summary>
    internal Func<FullReportTarget, FullReportResult>? FullTextReader { get; init; }

    public string Availability { get; init; } = "N/A — no safely attributed activity available.";

    /// <summary>
    /// True only when this projection came from a successful guarded source
    /// read. Monitoring failure and unavailable/unsafe projections are false.
    /// A healthy projection may legitimately contain no activity anchors.
    /// </summary>
    public bool IsHealthy { get; init; }

    public const string RetentionNotice =
        "Transient excerpts from a bounded tail (up to 64 KB) of the job log; older results may be unavailable. " +
        "Re-read on restart, never restored from SQLite. Source order is used; observation is not proof of current execution, independent QA, or user acceptance.";

    public const string HistoryRetentionNotice =
        "Transient, bounded job-specific history: every valid part parsed from the latest 5 MiB of the job log, " +
        "with at most 4,000 characters per part. Carried across a refresh only while the same verified job/source " +
        "generation, nondecreasing length, and exact overlapping source records prove continuity; entries outside " +
        "the current reader window are dropped. Cleared on restart, source replacement, truncation, conflict, " +
        "unavailable/fallback scans, or missing overlap. This is not complete history, not job duration, and not " +
        "proof of execution, completion, independent QA, or user acceptance.";
}
