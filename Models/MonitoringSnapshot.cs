namespace DSMonitor.Models;

/// <summary>
/// One immutable reconciliation result produced by the monitoring service.
/// </summary>
public sealed record MonitoringSnapshot
{
    public required DateTimeOffset ObservedAt { get; init; }
    public MonitoringState State { get; init; } = MonitoringState.Idle;
    public bool ForgeDirectoryAvailable { get; init; }
    public IReadOnlyList<ForgeJobSnapshot> Jobs { get; init; } = Array.Empty<ForgeJobSnapshot>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }

    public int ActiveCount => Jobs.Count(job => job.Status.IsActive());
    public int CompletedCount => Jobs.Count(job => job.Status == ForgeJobStatus.Done);
    public int FailedOrTimedOutCount => Jobs.Count(job =>
        job.Status is ForgeJobStatus.Failed or ForgeJobStatus.TimedOut);
    public int UnknownCount => Jobs.Count(job => job.Status == ForgeJobStatus.Unknown);
    public int TotalCount => Jobs.Count;

    public double? ObservedCost
    {
        get
        {
            var costs = Jobs.Where(job => job.ObservedCost.HasValue)
                .Select(job => job.ObservedCost!.Value)
                .ToArray();
            return costs.Length == 0 ? null : costs.Sum();
        }
    }
}
