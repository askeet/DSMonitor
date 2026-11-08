namespace DSMonitor.Models;

public enum MonitoringEventSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public enum MonitoringEventCategory
{
    Session,
    Step,
    Output,
    Tool,
    File,
    Test,
    Permission,
    Error,
    Other
}

/// <summary>
/// A single observed forge/OpenCode log event. Summaries are deliberately
/// concise and contain no raw tool output or source content.
/// </summary>
public sealed record MonitoringEvent
{
    public required string EventKey { get; init; }
    public required string JobId { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public MonitoringEventCategory Category { get; init; }
    public MonitoringEventSeverity Severity { get; init; }
    public required string Summary { get; init; }

    public string TimestampText =>
        (Timestamp ?? ObservedAt).ToLocalTime().ToString("HH:mm:ss");

    public string TimestampTooltip => Timestamp is null
        ? $"Observed at {ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} (event timestamp unavailable)"
        : Timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

    public string CategoryText => Category switch
    {
        MonitoringEventCategory.Session => "Session",
        MonitoringEventCategory.Step => "Step",
        MonitoringEventCategory.Output => "Output",
        MonitoringEventCategory.Tool => "Tool",
        MonitoringEventCategory.File => "File",
        MonitoringEventCategory.Test => "Test",
        MonitoringEventCategory.Permission => "Permission",
        MonitoringEventCategory.Error => "Error",
        _ => "Event"
    };

    public string SeverityText => Severity switch
    {
        MonitoringEventSeverity.Success => "Success",
        MonitoringEventSeverity.Warning => "Warning",
        MonitoringEventSeverity.Error => "Error",
        _ => "Info"
    };
}

/// <summary>
/// Parsed log event plus the cost reported by the source event, when present.
/// </summary>
public sealed record ParsedLogEvent(string EventKey, MonitoringEvent Event, double? Cost);
