using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace DSMonitor.Services;

/// <summary>
/// Converts a JSON Unix-millisecond number to the exact DateTimeOffset tick it
/// represents. Fractional milliseconds are retained only when they map to a
/// whole tick; values requiring sub-tick precision or an unsupported range are
/// rejected instead of being rounded into a different timestamp.
/// </summary>
internal static class ExactUnixMillisecondTimestamp
{
    private const int MaxLiteralLength = 512;
    private const int MaxPowerMagnitude = 1_000;
    private const int MaxSignificandDigits = 100;
    private static readonly long EpochTicks = DateTimeOffset.UnixEpoch.UtcTicks;
    private static readonly long MaxTicks = DateTimeOffset.MaxValue.UtcTicks;

    public static bool TryParse(JsonElement element, out DateTimeOffset value)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            value = default;
            return false;
        }

        return TryParse(element.GetRawText(), out value);
    }

    internal static bool TryParse(string literal, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrEmpty(literal) || literal.Length > MaxLiteralLength)
        {
            return false;
        }

        var index = 0;
        var negative = false;
        if (literal[index] == '-')
        {
            negative = true;
            index++;
        }

        if (index >= literal.Length)
        {
            return false;
        }

        var exponentIndex = literal.IndexOfAny(['e', 'E'], index);
        var mantissaEnd = exponentIndex >= 0 ? exponentIndex : literal.Length;
        if (mantissaEnd <= index)
        {
            return false;
        }

        long exponent = 0;
        if (exponentIndex >= 0)
        {
            var exponentText = literal[(exponentIndex + 1)..];
            if (exponentText.Length == 0 ||
                !long.TryParse(
                    exponentText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent) ||
                exponent < -MaxPowerMagnitude ||
                exponent > MaxPowerMagnitude)
            {
                return false;
            }
        }

        var dotIndex = literal.IndexOf('.', index, mantissaEnd - index);
        var integerEnd = dotIndex >= 0 ? dotIndex : mantissaEnd;
        var integerText = literal[index..integerEnd];
        if (integerText.Length == 0 || !IsAsciiDigits(integerText))
        {
            return false;
        }

        var fractionText = dotIndex >= 0
            ? literal[(dotIndex + 1)..mantissaEnd]
            : string.Empty;
        if (dotIndex >= 0 && (fractionText.Length == 0 || !IsAsciiDigits(fractionText)))
        {
            return false;
        }

        var digits = integerText + fractionText;
        var trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0)
        {
            value = DateTimeOffset.UnixEpoch;
            return true;
        }

        if (trimmed.Length > MaxSignificandDigits)
        {
            return false;
        }

        var significand = BigInteger.Parse(
            trimmed,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        var scale = (long)fractionText.Length - exponent;
        var power = 4L - scale;
        if (power < -MaxPowerMagnitude || power > MaxPowerMagnitude)
        {
            return false;
        }

        BigInteger ticks;
        if (power >= 0)
        {
            ticks = significand * BigInteger.Pow(10, (int)power);
        }
        else
        {
            var divisor = BigInteger.Pow(10, (int)-power);
            if (!BigInteger.Remainder(significand, divisor).IsZero)
            {
                return false;
            }

            ticks = significand / divisor;
        }

        if (negative)
        {
            ticks = -ticks;
        }

        var absoluteTicks = ticks + EpochTicks;
        if (absoluteTicks < 0 || absoluteTicks > MaxTicks)
        {
            return false;
        }

        value = new DateTimeOffset((long)absoluteTicks, TimeSpan.Zero);
        return true;
    }

    private static bool IsAsciiDigits(string value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
