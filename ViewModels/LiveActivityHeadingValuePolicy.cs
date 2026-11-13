using System.Globalization;
using System.Text;

namespace DSMonitor.ViewModels;

/// <summary>
/// V1-only presentation policy shared by the Forge jobs table and Live Activity
/// history status. It shows only the first Markdown line, removes an initial
/// <c>##Status</c>/<c>##Result</c> marker and a leading <c>Status:</c> label,
/// unwraps a leading <c>**...**</c> span through its closing marker, and trims a
/// plain first line at its first sentence period. It is not the general A2
/// parser and performs no source access or completion inference.
/// </summary>
internal static class LiveActivityStatusLinePolicy
{
    public static string Tooltip(string value) => value;

    /// <summary>
    /// Returns the unverified first-line value, or null when no non-empty first
    /// line remains. A missing closing <c>**</c> is preserved safely rather
    /// than guessed or dropped.
    /// </summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lineEnd = text.IndexOfAny(['\r', '\n']);
        var value = (lineEnd >= 0 ? text[..lineEnd] : text).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var fromPairedBold = false;
        // Handle combinations in either order: ##Status: **DONE** and
        // **##Status: DONE**. Two passes cover marker-then-bold and
        // bold-then-marker without attempting general Markdown parsing.
        for (var pass = 0; pass < 2; pass++)
        {
            var before = value;
            value = StripInitialMarker(value);
            if (TryUnwrapLeadingBold(value, out var unwrapped))
            {
                value = unwrapped;
                fromPairedBold = true;
            }

            value = StripLeadingStatusLabel(value);
            if (string.Equals(value, before, StringComparison.Ordinal))
            {
                break;
            }
        }

        value = value.Trim();
        if (!fromPairedBold && !value.StartsWith("**", StringComparison.Ordinal))
        {
            value = TrimPlainSentence(value);
        }

        return value.Length == 0 ? null : value;
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
        if (index < value.Length &&
            value[index] is not (' ' or '\t' or ':'))
        {
            // Do not strip a longer word such as ##Statusish.
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
            if (value[index] != '.')
            {
                continue;
            }

            if (index + 1 == value.Length || char.IsWhiteSpace(value[index + 1]))
            {
                return value[..index].TrimEnd();
            }
        }

        return value;
    }
}

/// <summary>
/// Display-safe projection of one complete activity text. It preserves line
/// breaks and tabs for the multiline viewer and removes control/format/
/// surrogate code points that cannot be displayed safely. It deliberately does
/// not apply the bounded history-preview character cap: only a guarded result
/// that the reader already classified as complete may reach this projection.
/// </summary>
internal static class LiveActivityTextPolicy
{
    public static string SanitizeForDisplay(string text, out bool sanitized)
    {
        ArgumentNullException.ThrowIfNull(text);
        sanitized = false;
        var builder = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                if (rune.Value is '\n' or '\r' or '\t')
                {
                    builder.Append((char)rune.Value);
                }
                else
                {
                    sanitized = true;
                }

                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
