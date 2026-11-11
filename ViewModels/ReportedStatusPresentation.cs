using System.Globalization;
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
/// </summary>
public static class ReportedStatusPresentation
{
    public const int MaxDisplayLength = 15;
    public const string Unavailable = "N/A";

    private const string UnverifiedTooltipSuffix =
        " — unverified agent-reported text; not authoritative Forge execution status.";

    private const string UnavailableTooltip =
        "N/A — unverified agent-reported status text is unavailable or not unambiguous; " +
        "not authoritative Forge execution status.";

    /// <summary>
    /// Runtime display: the reported value truncated to
    /// <see cref="MaxDisplayLength"/> characters, or the neutral placeholder.
    /// </summary>
    public static string ToDisplay(ReportedStatusExtraction? extraction) =>
        TryGetReportedValue(extraction, out var value)
            ? TruncateForDisplay(value)
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

    /// <summary>
    /// Truncates to at most <see cref="MaxDisplayLength"/> UTF-16 code units,
    /// replacing the final code unit with an ellipsis when the value is longer.
    /// Truncation follows text-element boundaries so surrogate pairs and
    /// combining sequences are never split. If no complete element fits, the
    /// ellipsis alone is returned rather than unsafe partial content.
    /// </summary>
    private static string TruncateForDisplay(string value)
    {
        if (value.Length <= MaxDisplayLength)
        {
            return value;
        }

        var prefix = TakeTextElements(value, MaxDisplayLength - 1);
        return prefix.Length == 0 ? "…" : prefix + "…";
    }

    private static string TakeTextElements(string value, int maxCodeUnits)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        var end = 0;
        for (var index = 0; index < starts.Length; index++)
        {
            var next = index + 1 < starts.Length ? starts[index + 1] : value.Length;
            if (next > maxCodeUnits)
            {
                break;
            }

            end = next;
        }

        return value[..end];
    }
}
