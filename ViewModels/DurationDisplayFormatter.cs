using System.Globalization;

namespace DSMonitor.ViewModels;

/// <summary>
/// Shared table-duration display: total minutes plus seconds rounded to three
/// decimal places. Rounds the total seconds before splitting minutes so a value
/// such as 59.9995 s becomes <c>1m 0.000s</c>.
/// Uses decimal tick arithmetic to avoid binary floating-point drift and
/// overflow at large <see cref="TimeSpan"/> values.
/// </summary>
internal static class DurationDisplayFormatter
{
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "N/A";
        }

        var totalSeconds = duration.Ticks / (decimal)TimeSpan.TicksPerSecond;
        var roundedMilliseconds = decimal.ToInt64(
            decimal.Round(totalSeconds, 3, MidpointRounding.AwayFromZero) * 1000m);
        var minutes = roundedMilliseconds / 60_000;
        var remainder = roundedMilliseconds % 60_000;
        var seconds = remainder / 1000;
        var milliseconds = remainder % 1000;
        if (duration == TimeSpan.Zero)
        {
            return "0s";
        }

        if (minutes == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{seconds}.{milliseconds:000}s");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes}m {seconds}.{milliseconds:000}s");
    }
}
