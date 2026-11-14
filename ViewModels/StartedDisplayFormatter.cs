using System.Globalization;

namespace DSMonitor.ViewModels;

/// <summary>Shared local-time display for Started columns in both tables.</summary>
internal static class StartedDisplayFormatter
{
    public static string Format(DateTimeOffset? timestamp)
    {
        if (timestamp is not { } value)
        {
            return "N/A";
        }

        try
        {
            return value.ToLocalTime().ToString(
                "MMM d HH:mm:ss.fff",
                CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "N/A";
        }
    }
}
