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
    /// Absolute first byte of the successfully parsed bounded window. Together
    /// with <see cref="ReadWindowEnd"/> this proves which previously complete
    /// records are still visible when deciding whether history may be carried
    /// across a refresh. A negative end means no successful window was parsed.
    /// </summary>
    internal long ReadWindowStart { get; init; } = -1;

    /// <summary>Exclusive absolute end of the successfully parsed bounded window.</summary>
    internal long ReadWindowEnd { get; init; } = -1;

    internal bool HasReadWindow => ReadWindowEnd >= 0 && ReadWindowStart >= 0;

    public string Availability { get; init; } = "N/A — no safely attributed activity available.";
    public const string RetentionNotice =
        "Transient excerpts from a bounded tail (up to 64 KB) of the job log; older results may be unavailable. " +
        "Re-read on restart, never restored from SQLite. Source order is used; observation is not proof of current execution, independent QA, or user acceptance.";

    public const string HistoryRetentionNotice =
        "Transient, bounded job-specific history: at most 64 parts and 4,000 characters per part. " +
        "Carried across a refresh only while the same verified job/source generation, nondecreasing length, " +
        "and exact overlapping source records prove continuity. Cleared on restart, source replacement, " +
        "truncation, conflict, unavailable/fallback scans, or missing overlap. This is not complete history, " +
        "not job duration, and not proof of execution, completion, independent QA, or user acceptance.";
}
