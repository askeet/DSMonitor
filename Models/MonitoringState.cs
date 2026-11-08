namespace DSMonitor.Models;

/// <summary>
/// DSMonitor's own lifecycle state. This is not a forge job status and is not
/// proof of the state of an OpenCode worker.
/// </summary>
public enum MonitoringState
{
    Stopped,
    Idle,
    Monitoring,
    Error
}

public static class MonitoringStateExtensions
{
    public static string ToDisplay(this MonitoringState state) => state switch
    {
        MonitoringState.Stopped => "STOPPED",
        MonitoringState.Idle => "IDLE",
        MonitoringState.Monitoring => "MONITORING",
        MonitoringState.Error => "ERROR",
        _ => "STOPPED"
    };
}
