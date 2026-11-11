namespace DSMonitor.Models;

/// <summary>
/// Outcome of a guarded, bounded retrieval of one previously observed text
/// part. The caller must treat only <see cref="Complete"/> as full content.
/// </summary>
public enum FullReportStatus
{
    /// <summary>One complete eligible text record was captured and passed all guards.</summary>
    Complete,

    /// <summary>
    /// The target was located but its full original content cannot be proven:
    /// the bounded window clips it, the record exceeds the cap, a terminating
    /// newline is missing, the source was truncated, or a partial read occurred.
    /// </summary>
    Incomplete,

    /// <summary>
    /// No report is available: the job/source association, source, capture, or
    /// target part could not be found. Nothing is substituted.
    /// </summary>
    Unavailable,

    /// <summary>
    /// A security or integrity guard failed: ambiguous ownership, unsafe path or
    /// link, physical identity change, malformed/invalid UTF-8 target content,
    /// ineligible role/type, or a record that no longer matches the target.
    /// </summary>
    Unsafe
}

/// <summary>
/// One previously observed text part selected for full-report retrieval.
/// <paramref name="ExpectedByteOffset"/> is the source offset recorded for that
/// record. <paramref name="ExpectedRecordDigest"/> is the SHA-256 of the exact
/// raw record bytes captured at preview time. A complete result requires this
/// binding; when supplied, the reader requires the same record boundary and
/// content, and never substitutes a different part, offset, or rewritten record.
/// </summary>
public sealed record FullReportTarget(
    string JobId,
    string SessionId,
    string MessageId,
    string? PartId,
    long? ExpectedByteOffset = null,
    string? ExpectedRecordDigest = null)
{
    /// <summary>
    /// Build a target from a transient preview excerpt. A null part identity or
    /// digest remains explicit and makes full content ineligible rather than
    /// guessing.
    /// </summary>
    public static FullReportTarget FromExcerpt(ActivityExcerpt excerpt) => new(
        excerpt.JobId,
        excerpt.SessionId,
        excerpt.MessageId,
        excerpt.PartId,
        excerpt.ByteOffset,
        excerpt.RecordDigest);
}

/// <summary>
/// Transient result of one guarded, bounded text-part retrieval. It is never
/// persisted to the ledger, SQLite, or monitoring logs. Complete means the one
/// selected record was fully captured; it never implies job completion, tool
/// success, multipart assembly, or user acceptance.
/// </summary>
public sealed record FullReportResult(
    FullReportStatus Status,
    string Reason,
    string? Text,
    string JobId,
    string? SessionId,
    string? MessageId,
    string? PartId,
    string? SourcePath,
    long? ByteOffset,
    long? RecordLengthBytes,
    DateTimeOffset? SourceTimestamp,
    DateTimeOffset ObservedAt,
    string Kind)
{
    /// <summary>True only when the complete original text is present.</summary>
    public bool IsComplete => Status == FullReportStatus.Complete && Text is not null;

    /// <summary>
    /// Explanation for non-complete outcomes; for complete outcomes it records
    /// the guarded capture provenance.
    /// </summary>
    public string Provenance => Status == FullReportStatus.Complete
        ? $"{Kind}; job {JobId}; session {SessionId}; message {MessageId}; " +
          $"part {PartId}; source {SourcePath} at byte {ByteOffset}; " +
          $"record {RecordLengthBytes} bytes; source time {SourceTimestamp?.ToString("O") ?? "N/A"}; " +
          $"observed {ObservedAt:O}. Transient guarded capture of one text part; " +
          "no multipart assembly or success inference."
        : Reason;
}
