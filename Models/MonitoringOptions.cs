namespace DSMonitor.Models;

public sealed record MonitoringOptions(
    TimeSpan RefreshInterval,
    int MaxRecentEventsPerJob = 500,
    long MaxBytesPerPoll = 512 * 1024,
    long MaxLogLineBytes = 1024 * 1024)
{
    public static MonitoringOptions Default { get; } =
        new(TimeSpan.FromSeconds(1));
}
