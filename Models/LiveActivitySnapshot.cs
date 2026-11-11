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

    public string Availability { get; init; } = "N/A — no safely attributed activity available.";
    public const string RetentionNotice =
        "Transient excerpts from a bounded tail (up to 64 KB) of the job log; older results may be unavailable. " +
        "Re-read on restart, never restored from SQLite. Source order is used; observation is not proof of current execution, independent QA, or user acceptance.";
}
