using System.Globalization;
using System.IO;
using System.Text;
using DSMonitor.Models;

namespace DSMonitor.Services;

/// <summary>
/// Extracts one explicit, line-anchored <c>Status:</c> declaration from a
/// complete guarded full-report result. It is a pure in-memory parser: it never
/// opens a source, queries the ledger or SQLite, writes logs, persists content,
/// or changes Forge/job status. A roleless report remains eligible but
/// unverified. Multiple differing, malformed, or unsafe declarations are
/// ambiguous and never select a first or last value.
/// </summary>
public static class ReportedStatusParser
{
    private const string StatusToken = "Status";
    private const int MaxHeadingMarkers = 6;

    public static ReportedStatusExtraction Extract(FullReportResult report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.IsComplete || string.IsNullOrWhiteSpace(report.Text))
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.Ineligible,
                null,
                Array.Empty<ReportedStatusDeclaration>(),
                "N/A — the report is not a complete guarded text capture; no status is extracted.",
                report);
        }

        var declarations = new List<ReportedStatusDeclaration>();
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var inBlockquote = false;
        var inList = false;
        var listCanEnd = false;
        var uncertain = false;
        var lineNumber = 0;
        using var reader = new StringReader(report.Text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
            }

            // A valid top-level fenced block owns all content until its closing
            // marker. A closing marker with trailing text is not a closure.
            if (fenceCharacter != '\0')
            {
                TryConsumeFence(line, ref fenceCharacter, ref fenceLength);
                continue;
            }

            if (IsBlank(line))
            {
                inBlockquote = false;
                if (inList)
                {
                    listCanEnd = true;
                }

                continue;
            }

            // Conservative block quote handling: an explicit marker starts a
            // quote and non-blank unmarked lines remain lazy continuation until
            // a blank line. Status-like content there is never attributed.
            if (inBlockquote)
            {
                if (TryGetBlockQuoteMarker(line, out var quotedContent)
                        ? ContainsUnsupportedStatus(quotedContent)
                        : ContainsUnsupportedStatus(line.AsSpan()))
                {
                    uncertain = true;
                }

                continue;
            }

            // List items, nested lists and their lazy continuations are not
            // top-level declaration contexts. Keep the list open across blank
            // lines until an unindented non-list line proves it has ended.
            if (inList)
            {
                if (LeadingIndentColumns(line.AsSpan()) == 0 &&
                    listCanEnd &&
                    !TryGetListMarker(line.AsSpan(), out _))
                {
                    inList = false;
                    listCanEnd = false;
                }
                else
                {
                    if (ContainsUnsupportedStatus(line.AsSpan()))
                    {
                        uncertain = true;
                    }

                    listCanEnd = false;
                    continue;
                }
            }

            if (TryGetBlockQuoteMarker(line, out var quoteContent))
            {
                inBlockquote = true;
                if (ContainsUnsupportedStatus(quoteContent))
                {
                    uncertain = true;
                }

                continue;
            }

            if (TryGetListMarker(line.AsSpan(), out var listContent))
            {
                inList = true;
                listCanEnd = false;
                if (ContainsUnsupportedStatus(listContent))
                {
                    uncertain = true;
                }

                continue;
            }

            // Four columns, including tab expansion, are an indented Markdown
            // code block and cannot declare a reported status.
            if (LeadingIndentColumns(line.AsSpan()) >= 4)
            {
                continue;
            }

            if (IsFenceMarker(line.AsSpan()))
            {
                if (IsInvalidBacktickFenceOpener(line.AsSpan()))
                {
                    // An invalid opener is malformed Markdown context. Fail
                    // closed rather than letting following lines become top-level
                    // declarations by accident.
                    uncertain = true;
                    continue;
                }

                TryConsumeFence(line, ref fenceCharacter, ref fenceLength);
                continue;
            }

            if (TryParseDeclaration(line, lineNumber, out var declaration))
            {
                declarations.Add(declaration);
            }
        }

        if (uncertain)
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.Unavailable,
                null,
                declarations,
                "N/A — a quoted, list, fence, or otherwise unsupported Markdown context cannot be classified safely; no status is inferred.",
                report);
        }

        if (declarations.Count == 0)
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.NotReported,
                null,
                declarations,
                "No line-anchored explicit Status: declaration was found in the complete report.",
                report);
        }

        if (declarations.Any(declaration => declaration.IsMalformed))
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.Ambiguous,
                null,
                declarations,
                "A malformed, unsafe, or conflicting Status: declaration prevents selecting a value.",
                report);
        }

        var values = declarations
            .Where(declaration => !declaration.IsEmpty)
            .ToArray();
        if (values.Length == 0)
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.Empty,
                null,
                declarations,
                "Explicit Status: declaration(s) were present but supplied no value.",
                report);
        }

        var distinct = values
            .Select(declaration => declaration.Value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 1)
        {
            return new ReportedStatusExtraction(
                ReportedStatusState.Reported,
                distinct[0],
                declarations,
                "One explicit reported Status value was observed in the complete report.",
                report);
        }

        return new ReportedStatusExtraction(
            ReportedStatusState.Ambiguous,
            null,
            declarations,
            "Multiple differing Status: declarations prevent selecting a value.",
            report);
    }

    private static bool TryConsumeFence(
        string line,
        ref char fenceCharacter,
        ref int fenceLength)
    {
        var lineSpan = line.AsSpan();
        // A fence may be indented by at most three columns. Deeper indentation
        // is an indented code block, and inside a fence it is content rather
        // than a closing marker.
        if (LeadingIndentColumns(lineSpan) >= 4)
        {
            return fenceCharacter != '\0';
        }

        var span = TrimAsciiStart(lineSpan);
        if (span.IsEmpty)
        {
            return fenceCharacter != '\0';
        }

        var marker = span[0];
        if (marker is not ('`' or '~'))
        {
            return fenceCharacter != '\0';
        }

        var runLength = 0;
        while (runLength < span.Length && span[runLength] == marker)
        {
            runLength++;
        }

        if (runLength < 3)
        {
            return fenceCharacter != '\0';
        }

        if (fenceCharacter == '\0')
        {
            // A backtick fence's info string cannot contain a backtick. Treat
            // such a line as non-structural text rather than opening a fence.
            if (marker == '`' && span[runLength..].IndexOf('`') >= 0)
            {
                return true;
            }

            fenceCharacter = marker;
            fenceLength = runLength;
            return true;
        }

        if (marker == fenceCharacter &&
            runLength >= fenceLength &&
            IsAsciiWhitespaceOnly(span[runLength..]))
        {
            fenceCharacter = '\0';
            fenceLength = 0;
        }

        return true;
    }

    private static bool TryParseDeclaration(
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;
        var span = line.AsSpan();
        var index = 0;
        while (index < span.Length && IsIndent(span[index]))
        {
            index++;
        }

        var headingMarkers = 0;
        while (index < span.Length && span[index] == '#' && headingMarkers <= MaxHeadingMarkers)
        {
            headingMarkers++;
            index++;
        }

        if (headingMarkers > MaxHeadingMarkers)
        {
            return false;
        }

        if (headingMarkers > 0)
        {
            while (index < span.Length && IsIndent(span[index]))
            {
                index++;
            }
        }

        var rest = span[index..];
        if (rest.IsEmpty)
        {
            return false;
        }

        // Triple emphasis is ambiguous and is not guessed into bold or italic.
        if (rest.StartsWith("***".AsSpan(), StringComparison.Ordinal))
        {
            if (ContainsStatusToken(rest))
            {
                declaration = Malformed(lineNumber, line);
                return true;
            }

            return false;
        }

        if (rest.StartsWith("**".AsSpan(), StringComparison.Ordinal))
        {
            var close = rest[2..].IndexOf("**".AsSpan(), StringComparison.Ordinal);
            if (close >= 0)
            {
                var inner = rest.Slice(2, close);
                var after = rest[(2 + close + 2)..];
                return TryParseEmphasized(inner, after, line, lineNumber, out declaration);
            }

            return TryParsePlain(rest[2..], line, lineNumber, out declaration);
        }

        if (rest.StartsWith("*".AsSpan(), StringComparison.Ordinal))
        {
            var close = rest[1..].IndexOf("*".AsSpan(), StringComparison.Ordinal);
            if (close >= 0)
            {
                var inner = rest.Slice(1, close);
                var after = rest[(1 + close + 1)..];
                return TryParseEmphasized(inner, after, line, lineNumber, out declaration);
            }

            return TryParsePlain(rest[1..], line, lineNumber, out declaration);
        }

        return TryParsePlain(rest, line, lineNumber, out declaration);
    }

    private static bool TryParseEmphasized(
        ReadOnlySpan<char> inner,
        ReadOnlySpan<char> after,
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;
        inner = TrimAscii(inner);
        if (!TryMatchStatusKey(inner, out var keyRemainder))
        {
            return false;
        }

        keyRemainder = TrimAsciiStart(keyRemainder);
        if (keyRemainder.StartsWith(":".AsSpan(), StringComparison.Ordinal))
        {
            var valueText = keyRemainder[1..];
            if (valueText.Trim().IsEmpty)
            {
                // "**Status:**" or "**Status: **" is a label pair; a value may
                // follow the closing marker.
                var remainder = TrimAsciiStart(after);
                if (remainder.IsEmpty)
                {
                    declaration = Empty(lineNumber, line, paired: true);
                    return true;
                }

                if (remainder.StartsWith(":".AsSpan(), StringComparison.Ordinal))
                {
                    remainder = remainder[1..];
                }

                return TryParseValue(remainder, line, lineNumber, out declaration);
            }

            // "**Status: VALUE**" pairs the whole declaration. Preserve the
            // value's internal punctuation instead of applying unpaired stops.
            return CreatePairedValue(valueText, after, line, lineNumber, out declaration);
        }

        // "**Status**: VALUE" emphasizes only the label.
        if (keyRemainder.IsEmpty)
        {
            var remainder = TrimAsciiStart(after);
            if (!remainder.StartsWith(":".AsSpan(), StringComparison.Ordinal))
            {
                return false;
            }

            return TryParseValue(remainder[1..], line, lineNumber, out declaration);
        }

        return false;
    }

    private static bool TryParsePlain(
        ReadOnlySpan<char> rest,
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;
        rest = TrimAsciiStart(rest);
        if (!TryMatchStatusKey(rest, out var afterKey) ||
            !afterKey.StartsWith(":".AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        return TryParseValue(afterKey[1..], line, lineNumber, out declaration);
    }

    private static bool TryParseValue(
        ReadOnlySpan<char> raw,
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;

        if (ContainsStatusToken(raw))
        {
            declaration = Malformed(lineNumber, line);
            return true;
        }

        var span = raw.TrimStart();
        // Leading colons/whitespace are decorations. Remove them before pair
        // detection so ": **v1.2**" still preserves punctuation as a pair.
        while (!span.IsEmpty && span[0] == ':')
        {
            span = span[1..].TrimStart();
        }

        if (span.IsEmpty)
        {
            declaration = Empty(lineNumber, line, paired: false);
            return true;
        }

        if (span.StartsWith("**".AsSpan(), StringComparison.Ordinal))
        {
            var close = span[2..].IndexOf("**".AsSpan(), StringComparison.Ordinal);
            if (close >= 0)
            {
                var inner = span.Slice(2, close);
                var trailing = span[(2 + close + 2)..];
                return CreatePairedValue(inner, trailing, line, lineNumber, out declaration);
            }
        }
        else if (span.StartsWith("*".AsSpan(), StringComparison.Ordinal))
        {
            var close = span[1..].IndexOf("*".AsSpan(), StringComparison.Ordinal);
            if (close >= 0)
            {
                var inner = span.Slice(1, close);
                var trailing = span[(1 + close + 1)..];
                return CreatePairedValue(inner, trailing, line, lineNumber, out declaration);
            }
        }
        else if (TryGetQuotePair(span, out var quoteInner, out var quoteTrailing))
        {
            return CreatePairedValue(quoteInner, quoteTrailing, line, lineNumber, out declaration);
        }

        // Unpaired: trim leading markdown/quotes/colons/whitespace, then stop
        // at the first '.', '*', '#', or line end. A matched pair above is the
        // only path that preserves those characters inside the value.
        span = TrimLeadingDecoration(span);
        var stop = FirstUnpairedStop(span);
        if (stop >= 0)
        {
            span = span[..stop];
        }

        return CreateUnpairedValue(span, line, lineNumber, out declaration);
    }

    private static bool CreatePairedValue(
        ReadOnlySpan<char> rawValue,
        ReadOnlySpan<char> trailing,
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;
        if (ContainsStatusToken(rawValue) || ContainsStatusToken(trailing))
        {
            declaration = Malformed(lineNumber, line);
            return true;
        }

        var value = TrimOuterDecoration(rawValue);
        if (value.IsEmpty)
        {
            declaration = Empty(lineNumber, line, paired: true);
            return true;
        }

        if (!IsSafeValue(value))
        {
            declaration = Malformed(lineNumber, line);
            return true;
        }

        declaration = new ReportedStatusDeclaration(
            lineNumber,
            line,
            value.ToString(),
            IsPaired: true,
            IsEmpty: false,
            IsMalformed: false);
        return true;
    }

    private static bool CreateUnpairedValue(
        ReadOnlySpan<char> rawValue,
        string line,
        int lineNumber,
        out ReportedStatusDeclaration declaration)
    {
        declaration = null!;
        var value = TrimOuterDecoration(rawValue);
        if (value.IsEmpty)
        {
            declaration = Empty(lineNumber, line, paired: false);
            return true;
        }

        if (ContainsStatusToken(value) || !IsSafeValue(value))
        {
            declaration = Malformed(lineNumber, line);
            return true;
        }

        declaration = new ReportedStatusDeclaration(
            lineNumber,
            line,
            value.ToString(),
            IsPaired: false,
            IsEmpty: false,
            IsMalformed: false);
        return true;
    }

    private static bool TryMatchStatusKey(
        ReadOnlySpan<char> value,
        out ReadOnlySpan<char> remainder)
    {
        remainder = default;
        if (!value.StartsWith(StatusToken.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        remainder = value[StatusToken.Length..];
        return true;
    }

    private static bool TryGetQuotePair(
        ReadOnlySpan<char> span,
        out ReadOnlySpan<char> inner,
        out ReadOnlySpan<char> trailing)
    {
        inner = default;
        trailing = default;
        if (span.IsEmpty)
        {
            return false;
        }

        var closing = span[0] switch
        {
            '"' => '"',
            '\'' => '\'',
            '“' => '”',
            '‘' => '’',
            _ => '\0'
        };
        if (closing == '\0')
        {
            return false;
        }

        var closeIndex = span[1..].IndexOf(closing);
        if (closeIndex < 0)
        {
            return false;
        }

        inner = span.Slice(1, closeIndex);
        trailing = span[(1 + closeIndex + 1)..];
        return true;
    }

    private static int FirstUnpairedStop(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is '.' or '*' or '#')
            {
                return index;
            }
        }

        return -1;
    }

    private static ReadOnlySpan<char> TrimLeadingDecoration(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty && IsDecoration(value[0]))
        {
            value = value[1..];
        }

        return value;
    }

    private static ReadOnlySpan<char> TrimOuterDecoration(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsDecoration(value[start]))
        {
            start++;
        }

        while (end > start && IsDecoration(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsDecoration(char character) =>
        char.IsWhiteSpace(character) ||
        character is '*' or ':' or '"' or '\'' or '“' or '”' or '‘' or '’';

    private static bool IsIndent(char character) => character is ' ' or '\t';

    private static int LeadingIndentColumns(ReadOnlySpan<char> value)
    {
        var columns = 0;
        foreach (var character in value)
        {
            if (character == ' ')
            {
                columns++;
            }
            else if (character == '\t')
            {
                columns += 4 - (columns % 4);
            }
            else
            {
                break;
            }

            if (columns >= 4)
            {
                return 4;
            }
        }

        return columns;
    }

    private static bool IsAsciiWhitespaceOnly(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBlank(string line)
    {
        foreach (var character in line)
        {
            if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimAsciiStart(ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        return value[index..];
    }

    private static ReadOnlySpan<char> TrimAscii(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] is ' ' or '\t')
        {
            start++;
        }

        while (end > start && value[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static bool IsFenceMarker(ReadOnlySpan<char> value)
    {
        if (LeadingIndentColumns(value) >= 4)
        {
            return false;
        }

        var span = TrimAsciiStart(value);
        if (span.IsEmpty || span[0] is not ('`' or '~'))
        {
            return false;
        }

        var marker = span[0];
        var runLength = 0;
        while (runLength < span.Length && span[runLength] == marker)
        {
            runLength++;
        }

        return runLength >= 3;
    }

    private static bool IsInvalidBacktickFenceOpener(ReadOnlySpan<char> value)
    {
        if (LeadingIndentColumns(value) >= 4)
        {
            return false;
        }

        var span = TrimAsciiStart(value);
        if (span.IsEmpty || span[0] != '`')
        {
            return false;
        }

        var runLength = 0;
        while (runLength < span.Length && span[runLength] == '`')
        {
            runLength++;
        }

        return runLength >= 3 && span[runLength..].IndexOf('`') >= 0;
    }

    private static bool TryGetBlockQuoteMarker(
        string line,
        out ReadOnlySpan<char> content)
    {
        content = default;
        if (LeadingIndentColumns(line.AsSpan()) >= 4)
        {
            return false;
        }

        var span = TrimAsciiStart(line.AsSpan());
        if (span.IsEmpty || span[0] != '>')
        {
            return false;
        }

        content = TrimAsciiStart(span[1..]);
        return true;
    }

    private static bool TryGetListMarker(
        ReadOnlySpan<char> line,
        out ReadOnlySpan<char> content)
    {
        content = default;
        if (LeadingIndentColumns(line) >= 4)
        {
            return false;
        }

        var span = TrimAsciiStart(line);
        if (span.IsEmpty)
        {
            return false;
        }

        if (span[0] is '-' or '*' or '+')
        {
            if (span.Length == 1)
            {
                return true;
            }

            if (!IsIndent(span[1]))
            {
                return false;
            }

            content = TrimAsciiStart(span[2..]);
            return true;
        }

        var digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            digits++;
        }

        if (digits is < 1 or > 9 || digits >= span.Length)
        {
            return false;
        }

        if (span[digits] is not ('.' or ')'))
        {
            return false;
        }

        if (digits + 1 == span.Length)
        {
            return true;
        }

        if (!IsIndent(span[digits + 1]))
        {
            return false;
        }

        content = TrimAsciiStart(span[(digits + 2)..]);
        return true;
    }

    private static bool ContainsUnsupportedStatus(ReadOnlySpan<char> value)
    {
        if (IsFenceMarker(value) || ContainsStatusToken(value))
        {
            return true;
        }

        return TryGetListMarker(value, out var listContent) &&
               (IsFenceMarker(listContent) || ContainsStatusToken(listContent));
    }

    private static bool ContainsStatusToken(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty)
        {
            var index = value.IndexOf(StatusToken.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var after = value[(index + StatusToken.Length)..];
            var before = index == 0 ? '\0' : value[index - 1];
            var labelEnd = 0;
            // Match the supported emphasized label grammar too: **Status**:
            // and *Status*: are declarations even though the colon is outside
            // the emphasis pair. The token itself must not be an embedded
            // substring such as BuildStatus or MyStatus.
            while (labelEnd < after.Length &&
                   (after[labelEnd] == '*' || char.IsWhiteSpace(after[labelEnd])))
            {
                labelEnd++;
            }

            if (!IsWordCharacter(before) &&
                labelEnd < after.Length &&
                after[labelEnd] == ':')
            {
                return true;
            }

            value = after;
        }

        return false;
    }

    private static bool IsSafeValue(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }

            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }
        }

        return true;
    }

    private static ReportedStatusDeclaration Empty(int lineNumber, string line, bool paired) =>
        new(lineNumber, line, null, paired, IsEmpty: true, IsMalformed: false);

    private static ReportedStatusDeclaration Malformed(int lineNumber, string line) =>
        new(lineNumber, line, null, IsPaired: false, IsEmpty: false, IsMalformed: true);
}
