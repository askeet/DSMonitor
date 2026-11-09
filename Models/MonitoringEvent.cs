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
/// Structured, upstream-assigned identity used by the durable B2 projection.
/// The namespace is the capture protocol (currently OpenCode JSON lines), not
/// a provider or model name. Missing message/part values are represented by
/// <see cref="HasMessageAndPart"/> being false; no hash or local GUID is used
/// as a substitute for an unavailable upstream identity.
/// </summary>
public sealed record ParsedEventIdentity(
    string Namespace,
    string? SessionId,
    string? MessageId,
    string? PartId,
    string CanonicalEventType)
{
    public bool HasMessageAndPart =>
        !string.IsNullOrWhiteSpace(MessageId) &&
        !string.IsNullOrWhiteSpace(PartId) &&
        !string.IsNullOrWhiteSpace(CanonicalEventType);
}

/// <summary>
/// Parsed log event plus the cost reported by the source event, when present.
/// <see cref="CostAmount"/> preserves decimal precision for the durable ledger;
/// <see cref="Cost"/> remains the bounded display/decimal-compatible value used
/// by the existing live pipeline. <see cref="CostExpected"/> is true for
/// step-finish records, where an absent cost is a coverage gap rather than an
/// ordinary event that has no cost by design.
/// </summary>
public sealed record ParsedLogEvent(string EventKey, MonitoringEvent Event, double? Cost)
{
    public ParsedEventIdentity? Identity { get; init; }

    public decimal? CostAmount { get; init; }

    public bool CostExpected { get; init; }
}
