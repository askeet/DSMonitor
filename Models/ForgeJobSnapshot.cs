namespace DSMonitor.Models;

/// <summary>
/// Immutable observation of one forge job at a point in time.
/// </summary>
public sealed record ForgeJobSnapshot
{
    public required ForgeJobMetadata Metadata { get; init; }
    public ForgeJobStatus Status { get; init; } = ForgeJobStatus.Unknown;
    public string? SessionId { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public int ParsedEventCount { get; init; }
    public double? ObservedCost { get; init; }

    /// <summary>
    /// True once any event with a missing timestamp has been observed for this
    /// monitoring run. The bounded source-window retention policy then remains
    /// in force even after the barrier itself leaves the visible window.
    /// </summary>
    public bool HasObservedAmbiguousTimestamps { get; init; }

    public IReadOnlyList<MonitoringEvent> Events { get; init; } = Array.Empty<MonitoringEvent>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; init; }

    public string JobId => Metadata.JobId;
    public string StatusText => Status.ToDisplay();
    public bool HasCost => ObservedCost.HasValue;
    public bool DeadlinePassed =>
        Metadata.Deadline is { } deadline && deadline <= ObservedAt;

    public string CostText => ObservedCost is { } cost
        ? "$" + cost.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    public string DeadlineText => Metadata.Deadline is { } deadline
        ? deadline.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "—";

    public string DeadlineDetail => Metadata.Deadline is null
        ? "Not reported"
        : DeadlinePassed
            ? "Forge wrapper deadline passed. This is not proof that the OpenCode worker stopped."
            : "Forge wrapper deadline not reached.";
}
