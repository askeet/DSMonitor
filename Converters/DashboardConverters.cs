using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DSMonitor.Models;

namespace DSMonitor.Converters;

internal static class DashboardBrushes
{
    public static readonly Brush Muted = Frozen("#94A3B8");
    public static readonly Brush Blue = Frozen("#60A5FA");
    public static readonly Brush Green = Frozen("#22C55E");
    public static readonly Brush Emerald = Frozen("#34D399");
    public static readonly Brush Amber = Frozen("#FBBF24");
    public static readonly Brush Orange = Frozen("#FB923C");
    public static readonly Brush Red = Frozen("#F87171");

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed class MonitoringStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is MonitoringState state
            ? state switch
            {
                MonitoringState.Monitoring => DashboardBrushes.Green,
                MonitoringState.Idle => DashboardBrushes.Blue,
                MonitoringState.Error => DashboardBrushes.Red,
                _ => DashboardBrushes.Muted
            }
            : DashboardBrushes.Muted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ForgeStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ForgeJobStatus status
            ? status switch
            {
                ForgeJobStatus.Running => DashboardBrushes.Green,
                ForgeJobStatus.Done => DashboardBrushes.Emerald,
                ForgeJobStatus.Failed => DashboardBrushes.Red,
                ForgeJobStatus.Interrupted => DashboardBrushes.Amber,
                ForgeJobStatus.TimedOut => DashboardBrushes.Orange,
                ForgeJobStatus.Blocked => DashboardBrushes.Amber,
                _ => DashboardBrushes.Muted
            }
            : DashboardBrushes.Muted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EventSeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is MonitoringEventSeverity severity
            ? severity switch
            {
                MonitoringEventSeverity.Success => DashboardBrushes.Emerald,
                MonitoringEventSeverity.Warning => DashboardBrushes.Amber,
                MonitoringEventSeverity.Error => DashboardBrushes.Red,
                _ => DashboardBrushes.Blue
            }
            : DashboardBrushes.Blue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
