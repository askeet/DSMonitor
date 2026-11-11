using DSMonitor.Models;

namespace DSMonitor.ViewModels;

/// <summary>
/// V1 presentation policy for agent-reported status text.
///
/// The reported value is an unverified observation taken from the job-scoped
/// agent text; it is never authoritative Forge execution status, completion,
/// independent QA, or user acceptance. Only one unambiguous non-empty
/// <see cref="ReportedStatusState.Reported"/> value is shown. Missing,
/// not-reported, empty, ambiguous, unavailable, and ineligible extractions
/// render a neutral <c>N/A</c> placeholder.
///
/// The complete value is preserved. The table relies on WPF
/// <c>TextTrimming</c> for visual ellipsis and keeps the full text in the
/// tooltip; presentation code no longer applies a fixed character limit.
/// </summary>
public static class ReportedStatusPresentation
{
    public const string Unavailable = "N/A";

    private const string UnverifiedTooltipSuffix =
        " — unverified agent-reported text; not authoritative Forge execution status.";

    private const string UnavailableTooltip =
        "N/A — unverified agent-reported status text is unavailable or not unambiguous; " +
        "not authoritative Forge execution status.";

    /// <summary>
    /// Runtime display: the complete reported value, or the neutral
    /// placeholder. The view applies ellipsis; no character limit is imposed.
    /// </summary>
    public static string ToDisplay(ReportedStatusExtraction? extraction) =>
        TryGetReportedValue(extraction, out var value)
            ? value
            : Unavailable;

    /// <summary>
    /// Runtime tooltip: the complete, untruncated reported value with a clear
    /// unverified-agent-reported label, or a neutral placeholder explanation.
    /// </summary>
    public static string ToTooltip(ReportedStatusExtraction? extraction) =>
        TryGetReportedValue(extraction, out var value)
            ? value + UnverifiedTooltipSuffix
            : UnavailableTooltip;

    /// <summary>
    /// Returns one unambiguous non-empty reported value. The value is still
    /// unverified: role evidence is not treated as authoritative status.
    /// </summary>
    private static bool TryGetReportedValue(
        ReportedStatusExtraction? extraction,
        out string value)
    {
        value = string.Empty;
        if (extraction is null ||
            extraction.State != ReportedStatusState.Reported ||
            string.IsNullOrWhiteSpace(extraction.Value))
        {
            return false;
        }

        value = extraction.Value;
        return true;
    }
}
