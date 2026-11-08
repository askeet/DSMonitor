namespace DSMonitor.Models;

/// <summary>
/// Status of a forge-delegate job. This is deliberately separate from the
/// OpenCode worker/session state and from the DSMonitor monitoring state.
/// </summary>
public enum ForgeJobStatus
{
    Unknown,
    Running,
    Done,
    Failed,
    Interrupted,
    TimedOut,
    Blocked
}

public static class ForgeJobStatusExtensions
{
    public static bool IsActive(this ForgeJobStatus status) =>
        status is ForgeJobStatus.Running or ForgeJobStatus.Blocked;

    public static string ToDisplay(this ForgeJobStatus status) => status switch
    {
        ForgeJobStatus.Unknown => "UNKNOWN",
        ForgeJobStatus.Running => "RUNNING",
        ForgeJobStatus.Done => "DONE",
        ForgeJobStatus.Failed => "FAILED",
        ForgeJobStatus.Interrupted => "INTERRUPTED",
        ForgeJobStatus.TimedOut => "TIMED OUT",
        ForgeJobStatus.Blocked => "BLOCKED",
        _ => "UNKNOWN"
    };

    public static bool TryParse(string? value, out ForgeJobStatus status)
    {
        status = ForgeJobStatus.Unknown;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        switch (normalized)
        {
            case "running":
            case "in-progress":
            case "inprogress":
            case "active":
                status = ForgeJobStatus.Running;
                return true;
            case "done":
            case "complete":
            case "completed":
            case "success":
            case "succeeded":
                status = ForgeJobStatus.Done;
                return true;
            case "failed":
            case "failure":
            case "error":
                status = ForgeJobStatus.Failed;
                return true;
            case "interrupted":
            case "cancelled":
            case "canceled":
            case "aborted":
                status = ForgeJobStatus.Interrupted;
                return true;
            case "timed-out":
            case "timedout":
            case "timeout":
                status = ForgeJobStatus.TimedOut;
                return true;
            case "blocked":
            case "waiting-for-permission":
            case "permission":
                status = ForgeJobStatus.Blocked;
                return true;
            case "unknown":
            case "unavailable":
                status = ForgeJobStatus.Unknown;
                return true;
            default:
                return false;
        }
    }
}
