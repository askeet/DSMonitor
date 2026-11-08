using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class MonitoringRunLoggerTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Open_CreatesDatedUniqueLogAndJsonl()
    {
        var started = DateTimeOffset.Parse("2026-11-08T19:30:00+00:00");
        using var first = MonitoringRunLogger.Open(_workspace.LogsRoot, started);
        using var second = MonitoringRunLogger.Open(_workspace.LogsRoot, started);

        Assert.IsTrue(File.Exists(first.LogFilePath));
        Assert.IsTrue(File.Exists(first.JsonlFilePath));
        Assert.IsTrue(File.Exists(second.LogFilePath));
        Assert.IsTrue(File.Exists(second.JsonlFilePath));
        Assert.AreNotEqual(first.LogFilePath, second.LogFilePath);
        Assert.AreNotEqual(first.JsonlFilePath, second.JsonlFilePath);

        var expectedDay = started.ToLocalTime().ToString("yyyy-MM-dd");
        Assert.AreEqual(expectedDay, Path.GetFileName(Path.GetDirectoryName(first.LogFilePath)));
    }

    [TestMethod]
    public void Logger_FlushesImportantEventsPromptly()
    {
        using var logger = MonitoringRunLogger.Open(_workspace.LogsRoot, DateTimeOffset.UtcNow);
        logger.RunStarted(DateTimeOffset.UtcNow);

        var text = ReadAllTextShared(logger.LogFilePath!);
        var jsonLines = ReadAllLinesShared(logger.JsonlFilePath!);

        StringAssert.Contains(text, "Monitoring started");
        Assert.AreEqual(1, jsonLines.Length);
        using var document = JsonDocument.Parse(jsonLines[0]);
        Assert.AreEqual("monitoring_started", document.RootElement.GetProperty("kind").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("runId").GetString()));
    }

    [TestMethod]
    public void Logger_WritesJobSessionAndEventSummariesToJsonl()
    {
        using var logger = MonitoringRunLogger.Open(_workspace.LogsRoot, DateTimeOffset.UtcNow);
        logger.JobDiscovered(new ForgeJobMetadata
        {
            JobId = "job_logger_0001",
            MetadataPath = "ignored",
            ExplicitStatus = ForgeJobStatus.Unknown
        });
        logger.SessionObserved("job_logger_0001", "ses_123");
        logger.EventsObserved(new[]
        {
            new MonitoringEvent
            {
                EventKey = "key",
                JobId = "job_logger_0001",
                SessionId = "ses_123",
                Timestamp = DateTimeOffset.Parse("2026-11-08T10:00:00Z"),
                ObservedAt = DateTimeOffset.UtcNow,
                Category = MonitoringEventCategory.Tool,
                Severity = MonitoringEventSeverity.Success,
                Summary = "Tool read completed"
            }
        });
        logger.RunStopped(DateTimeOffset.UtcNow);

        var entries = ReadAllLinesShared(logger.JsonlFilePath!)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();

        Assert.IsTrue(entries.Any(entry =>
            entry.RootElement.GetProperty("kind").GetString() == "job_discovered"));
        Assert.IsTrue(entries.Any(entry =>
            entry.RootElement.GetProperty("kind").GetString() == "session_observed" &&
            entry.RootElement.GetProperty("sessionId").GetString() == "ses_123"));
        Assert.IsTrue(entries.Any(entry =>
            entry.RootElement.GetProperty("kind").GetString() == "log_event" &&
            entry.RootElement.GetProperty("summary").GetString() == "Tool read completed"));

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    [TestMethod]
    public void Logger_SanitizesControlCharactersInSummaries()
    {
        using var logger = MonitoringRunLogger.Open(_workspace.LogsRoot, DateTimeOffset.UtcNow);
        logger.Warning("first line\r\nsecond line");

        var text = ReadAllTextShared(logger.LogFilePath!);

        Assert.IsFalse(text.Contains("first line\r\nsecond line", StringComparison.Ordinal));
        StringAssert.Contains(text, "first line");
        StringAssert.Contains(text, "second line");
    }

    [TestMethod]
    public void Logger_AppendsDuringRepeatedLifecycle_WithoutOverwriting()
    {
        using (var first = MonitoringRunLogger.Open(_workspace.LogsRoot, DateTimeOffset.UtcNow))
        {
            first.RunStarted(DateTimeOffset.UtcNow);
            first.RunStopped(DateTimeOffset.UtcNow);
        }

        using (var second = MonitoringRunLogger.Open(_workspace.LogsRoot, DateTimeOffset.UtcNow))
        {
            second.RunStarted(DateTimeOffset.UtcNow);
            second.RunStopped(DateTimeOffset.UtcNow);
        }

        var logs = Directory.GetFiles(_workspace.LogsRoot, "*.log", SearchOption.AllDirectories);
        var jsonls = Directory.GetFiles(_workspace.LogsRoot, "*.jsonl", SearchOption.AllDirectories);
        Assert.AreEqual(2, logs.Length);
        Assert.AreEqual(2, jsonls.Length);
        Assert.IsTrue(logs.All(path => ReadAllTextShared(path).Contains("Monitoring started")));
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }
}
