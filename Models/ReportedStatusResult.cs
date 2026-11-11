using System.Globalization;

namespace DSMonitor.Models;

/// <summary>
/// Outcome of extracting an explicit <c>Status:</c> declaration from one
/// complete, guarded full-report result. Only <see cref="Reported"/> carries a
/// value; every other state deliberately leaves it null.
/// </summary>
public enum ReportedStatusState
{
    /// <summary>
    /// The supplied result is not a complete guarded text capture. Incomplete,
    /// unavailable, unsafe, or empty results are never partially parsed.
    /// </summary>
    Ineligible,

    /// <summary>
    /// The report is complete, but a quoted, list, fence, or otherwise unknown
    /// Markdown context cannot be classified safely. No status is inferred.
    /// </summary>
    Unavailable,

    /// <summary>No line-anchored explicit Status: declaration was found.</summary>
    NotReported,

    /// <summary>At least one declaration was present, but none supplied a value.</summary>
    Empty,

    /// <summary>Exactly one distinct non-empty value was reported.</summary>
    Reported,

    /// <summary>
    /// Multiple differing values, conflicting duplicates, or a malformed or
    /// unsafe declaration prevent selecting any value.
    /// </summary>
    Ambiguous
}

/// <summary>
/// One line-anchored declaration observed in the complete report text. Values
/// are transient observations; they are never persisted and never establish
/// authoritative job status, completion, QA, or user acceptance.
/// </summary>
public sealed record ReportedStatusDeclaration(
    int LineNumber,
    string RawLine,
    string? Value,
    bool IsPaired,
    bool IsEmpty,
    bool IsMalformed);

/// <summary>
/// Transient result of extracting a reported status from an already retrieved
/// complete <see cref="FullReportResult"/>. The parser performs no source read,
/// no ledger or SQLite access, no persistence, and no status mutation. The
/// original source result is retained so its job/message/part identity,
/// timestamps, path, byte offset, and role provenance remain attached.
/// </summary>
public sealed record ReportedStatusExtraction(
    ReportedStatusState State,
    string? Value,
    IReadOnlyList<ReportedStatusDeclaration> Declarations,
    string Reason,
    FullReportResult Source)
{
    /// <summary>
    /// True only for the reader's verified assistant text kind. Unknown kinds
    /// fail closed as unverified rather than becoming trusted.
    /// </summary>
    public bool IsRoleVerified => Source.Kind.StartsWith(
        "Assistant-reported text", StringComparison.Ordinal);

    /// <summary>True unless the source report carried verified assistant role evidence.</summary>
    public bool IsUnverified => !IsRoleVerified;

    /// <summary>True only for one unambiguous reported value.</summary>
    public bool HasValue => State == ReportedStatusState.Reported && Value is not null;

    /// <summary>
    /// Source provenance plus declaration-line evidence. It intentionally does
    /// not repeat the report text or declaration values.
    /// </summary>
    public string Provenance
    {
        get
        {
            var lines = Declarations.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    Declarations.Select(
                        declaration => declaration.LineNumber.ToString(CultureInfo.InvariantCulture)));
            return $"{Source.Provenance} Reported status extraction: {State}. " +
                   $"{Reason} Declaration lines: {lines}. " +
                   "Reported status text is an observation only; it is not authoritative job status, " +
                   "completion, independent QA, or user acceptance.";
        }
    }
}
