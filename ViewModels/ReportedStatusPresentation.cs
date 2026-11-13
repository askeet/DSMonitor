using DSMonitor.Models;

namespace DSMonitor.ViewModels;

/// <summary>
/// V1 presentation policy for agent-reported status text.
///
/// The reported value is an unverified observation taken from the job-scoped
/// agent text; it is never authoritative Forge execution status, completion,
/// independent QA, or user acceptance. An unambiguous parsed value takes
/// precedence. When none exists, a job-owned fallback text is normalized by
/// this same presentation function; a missing or unusable result renders
/// <c>N/A</c>.
///
/// <see cref="ResolveJobReportedStatus"/> is the single job-level policy entry
/// point: the Forge jobs table cell and the job's Live Activity history rows
/// reuse one resolved value instead of duplicating candidate selection.
///
/// The complete value is preserved. The table relies on WPF
/// <c>TextTrimming</c> for visual ellipsis and keeps the full text in the
/// tooltip; presentation code no longer applies a fixed character limit.
/// </summary>
public static class ReportedStatusPresentation
{
    public const string Unavailable = "N/A";

    /// <summary>
    /// One resolved job-level reported value bound to the job that owns it.
    /// The value remains an unverified observation.
    /// </summary>
    public readonly record struct JobReportedStatus(string JobId, string Value);

    /// <summary>
    /// Resolves the job-level value once from the job's own candidate sources:
    /// one unambiguous parsed value, then the extraction's job-owned source
    /// text, then the job-owned text activity. A missing or unusable result is
    /// <see cref="Unavailable"/>. Callers reuse the result instead of selecting
    /// candidates again, and must not apply it to another job's row.
    /// </summary>
    public static JobReportedStatus ResolveJobReportedStatus(ForgeJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var extraction = snapshot.ReportedStatus;
        if (TryGetReportedValue(extraction, out var value))
        {
            return new JobReportedStatus(snapshot.JobId, value);
        }

        if (extraction?.Source is { } source &&
            string.Equals(source.JobId, snapshot.JobId, StringComparison.Ordinal) &&
            TryExtractTextValue(source.Text, out value))
        {
            return new JobReportedStatus(snapshot.JobId, value);
        }

        if (snapshot.LiveActivity.Doing is { } doing &&
            string.Equals(doing.JobId, snapshot.JobId, StringComparison.Ordinal) &&
            doing.Kind.Contains("text", StringComparison.OrdinalIgnoreCase) &&
            TryExtractTextValue(doing.Text, out value))
        {
            return new JobReportedStatus(snapshot.JobId, value);
        }

        return new JobReportedStatus(snapshot.JobId, Unavailable);
    }

    /// <summary>
    /// Runtime display: a parsed reported value, a normalized fallback text
    /// value, or the neutral placeholder. The view applies ellipsis; no
    /// character limit is imposed here.
    /// </summary>
    public static string ToDisplay(
        ReportedStatusExtraction? extraction,
        string? fallbackText = null)
    {
        if (TryGetReportedValue(extraction, out var value))
        {
            return value;
        }

        return TryExtractTextValue(fallbackText, out value)
            ? value
            : Unavailable;
    }

    /// <summary>
    /// Runtime tooltip: the complete reported value or the same neutral
    /// placeholder shown in the cell.
    /// </summary>
    public static string ToTooltip(ReportedStatusExtraction? extraction) => ToDisplay(extraction);

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

    private static bool TryExtractTextValue(string? text, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var lineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = (lineEnd >= 0 ? text[..lineEnd] : text).Trim();
        if (firstLine.Length == 0)
        {
            return false;
        }

        if (TryNormalizeLine(firstLine, out value))
        {
            return true;
        }

        // A bare "## Status"/"## Result" heading owns no value of its own. The
        // reported value may be the immediately following non-empty paragraph
        // (for example a bold "**INCOMPLETE**" line). Only that next non-empty
        // line is considered; later narrative is never guessed or scanned.
        if (lineEnd < 0 ||
            !IsBareStatusHeading(firstLine) ||
            !TryGetNextNonEmptyLine(text, lineEnd + 1, out var followingLine))
        {
            return false;
        }

        return TryNormalizeLine(followingLine, out value);
    }

    private static bool TryNormalizeLine(string line, out string value)
    {
        value = string.Empty;
        var fromPairedBold = false;
        // Handle combinations in either order: ##Status: **DONE** and
        // **##Status: DONE**, without parsing general Markdown.
        for (var pass = 0; pass < 2; pass++)
        {
            var before = line;
            line = StripInitialMarker(line);
            if (TryUnwrapLeadingBold(line, out var unwrapped))
            {
                line = unwrapped;
                fromPairedBold = true;
            }

            line = StripLeadingStatusLabel(line);
            if (string.Equals(line, before, StringComparison.Ordinal))
            {
                break;
            }
        }

        line = line.Trim();
        if (!fromPairedBold && !line.StartsWith("**", StringComparison.Ordinal))
        {
            line = TrimPlainSentence(line);
        }

        if (line.Length == 0)
        {
            return false;
        }

        value = line;
        return true;
    }

    private static bool IsBareStatusHeading(string line) =>
        StripInitialMarker(line.Trim()).Length == 0;

    private static bool TryGetNextNonEmptyLine(string text, int start, out string line)
    {
        line = string.Empty;
        var index = start;
        while (index < text.Length)
        {
            if (text[index] is '\r' or '\n')
            {
                index++;
                continue;
            }

            var end = text.IndexOfAny(['\r', '\n'], index);
            var candidate = (end >= 0 ? text[index..end] : text[index..]).Trim();
            if (candidate.Length > 0)
            {
                line = candidate;
                return true;
            }

            if (end < 0)
            {
                return false;
            }

            index = end + 1;
        }

        return false;
    }

    private static string StripInitialMarker(string value)
    {
        if (!value.StartsWith("##", StringComparison.Ordinal))
        {
            return value;
        }

        var index = 2;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        var token = value.AsSpan(index);
        var tokenLength = token.StartsWith("Status", StringComparison.OrdinalIgnoreCase)
            ? "Status".Length
            : token.StartsWith("Result", StringComparison.OrdinalIgnoreCase)
                ? "Result".Length
                : 0;
        if (tokenLength == 0)
        {
            return value;
        }

        index += tokenLength;
        if (index < value.Length && value[index] is not (' ' or '\t' or ':'))
        {
            return value;
        }

        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        if (index < value.Length && value[index] == ':')
        {
            index++;
        }

        while (index < value.Length && value[index] is ' ' or '\t' or ':')
        {
            index++;
        }

        return value[index..].TrimStart();
    }

    private static bool TryUnwrapLeadingBold(string value, out string unwrapped)
    {
        unwrapped = value;
        if (!value.StartsWith("**", StringComparison.Ordinal))
        {
            return false;
        }

        var close = value.IndexOf("**", 2, StringComparison.Ordinal);
        if (close <= 2)
        {
            return false;
        }

        unwrapped = value[2..close].Trim();
        return true;
    }

    private static string StripLeadingStatusLabel(string value)
    {
        if (!value.StartsWith("Status", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var index = "Status".Length;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        if (index >= value.Length || value[index] != ':')
        {
            return value;
        }

        index++;
        while (index < value.Length && value[index] is ' ' or '\t' or ':')
        {
            index++;
        }

        return value[index..].TrimStart();
    }

    private static string TrimPlainSentence(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '.' &&
                (index + 1 == value.Length || char.IsWhiteSpace(value[index + 1])))
            {
                return value[..index].TrimEnd();
            }
        }

        return value;
    }
}
