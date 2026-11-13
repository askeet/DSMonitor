using System.Globalization;
using System.Text;

namespace DSMonitor.ViewModels;

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
