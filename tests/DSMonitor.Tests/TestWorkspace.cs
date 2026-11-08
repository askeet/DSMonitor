using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DSMonitor.Tests;

/// <summary>
/// Isolated temp workspace for every test. Only DSMonitor-owned test files are
/// written here; real forge/OpenCode data is never touched.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "DSMonitor.Tests",
            Guid.NewGuid().ToString("N"));
        RunsRoot = Path.Combine(Root, "runs");
        LogsRoot = Path.Combine(Root, "logs");
        Directory.CreateDirectory(RunsRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string Root { get; }
    public string RunsRoot { get; }
    public string LogsRoot { get; }
    public string OutsidePath => Path.Combine(Root, "outside", "secret.log");

    public string WriteMetadata(
        string jobId,
        long? started = null,
        long? deadline = null,
        string? status = null,
        string? logPath = null,
        string? model = "deepseek/deepseek-flash",
        string? directory = @"C:\work\sample")
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = jobId,
            ["pid"] = 4242,
            ["model"] = model,
            ["directory"] = directory,
            ["started"] = started ?? 1_800_000_000_000,
            ["deadline"] = deadline ?? 1_800_000_600_000,
            ["logPath"] = logPath ?? Path.Combine(RunsRoot, jobId + ".log")
        };

        if (status is not null)
        {
            payload["status"] = status;
        }

        var path = Path.Combine(RunsRoot, jobId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload), Encoding.UTF8);
        return path;
    }

    public string WriteRawMetadata(string fileName, string content)
    {
        var path = Path.Combine(RunsRoot, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    public string LogPath(string jobId) => Path.Combine(RunsRoot, jobId + ".log");

    public void WriteLog(string jobId, params string[] lines) =>
        File.WriteAllText(LogPath(jobId), string.Join("\n", lines) + "\n", Encoding.UTF8);

    public void AppendLog(string jobId, params string[] lines) =>
        File.AppendAllText(LogPath(jobId), string.Join("\n", lines) + "\n", Encoding.UTF8);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static string StepStart(
        long timestamp,
        string session = "ses_test0001",
        string part = "prt_start_1",
        string message = "msg_1")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "step_start",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = part,
                ["sessionID"] = session,
                ["messageID"] = message,
                ["type"] = "step-start"
            }
        });
    }

    public static string Text(
        long timestamp,
        string text,
        string session = "ses_test0001",
        string part = "prt_text_1",
        string message = "msg_1")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = part,
                ["sessionID"] = session,
                ["messageID"] = message,
                ["type"] = "text",
                ["text"] = text
            }
        });
    }

    public static string ToolUse(
        long timestamp,
        string tool,
        string status = "completed",
        long? start = null,
        long? end = null,
        string session = "ses_test0001",
        string part = "prt_tool_1",
        string message = "msg_1")
    {
        var state = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["input"] = new Dictionary<string, object?> { ["command"] = "dotnet test" }
        };

        if (start is not null && end is not null)
        {
            state["time"] = new Dictionary<string, object?>
            {
                ["start"] = start,
                ["end"] = end
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = part,
                ["partID"] = part,
                ["sessionID"] = session,
                ["messageID"] = message,
                ["type"] = "tool",
                ["tool"] = tool,
                ["state"] = state
            }
        });
    }

    public static string StepFinish(
        long timestamp,
        double cost,
        string reason = "stop",
        string session = "ses_test0001",
        string part = "prt_finish_1",
        string message = "msg_1")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "step_finish",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = part,
                ["sessionID"] = session,
                ["messageID"] = message,
                ["type"] = "step-finish",
                ["reason"] = reason,
                ["cost"] = cost,
                ["tokens"] = new Dictionary<string, object?>
                {
                    ["input"] = 10,
                    ["output"] = 20,
                    ["reasoning"] = 0,
                    ["cache"] = 0
                }
            }
        });
    }

    public static long Timestamp(string dateTime) =>
        DateTimeOffset.Parse(dateTime, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
}
