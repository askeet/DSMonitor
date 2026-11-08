using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSMonitor.Models;

namespace DSMonitor.Services;

public interface IMonitoringLogger : IDisposable
{
    string? LogFilePath { get; }
    string? JsonlFilePath { get; }
    string? LastError { get; }
    void RunStarted(DateTimeOffset startedAt);
    void JobDiscovered(ForgeJobMetadata job);
    void JobStatusChanged(string jobId, ForgeJobStatus previous, ForgeJobStatus current);
    void SessionObserved(string jobId, string sessionId);
    void EventsObserved(IReadOnlyList<MonitoringEvent> events);
    void Warning(string summary);
    void RunStopped(DateTimeOffset stoppedAt);
}

public interface IMonitoringLoggerFactory
{
    IMonitoringLogger Create(DateTimeOffset startedAt);
}

public sealed class NullMonitoringLogger : IMonitoringLogger
{
    public static NullMonitoringLogger Instance { get; } = new();
    private NullMonitoringLogger() { }
    public string? LogFilePath => null;
    public string? JsonlFilePath => null;
    public string? LastError => null;
    public void RunStarted(DateTimeOffset startedAt) { }
    public void JobDiscovered(ForgeJobMetadata job) { }
    public void JobStatusChanged(string jobId, ForgeJobStatus previous, ForgeJobStatus current) { }
    public void SessionObserved(string jobId, string sessionId) { }
    public void EventsObserved(IReadOnlyList<MonitoringEvent> events) { }
    public void Warning(string summary) { }
    public void RunStopped(DateTimeOffset stoppedAt) { }
    public void Dispose() { }
}

/// <summary>
/// Creates one uniquely named human-readable log and JSONL file per Start.
/// Existing files are never overwritten.
/// </summary>
public sealed class MonitoringRunLoggerFactory : IMonitoringLoggerFactory
{
    private readonly string _logsRoot;
    private readonly Func<DateTimeOffset> _clock;

    public MonitoringRunLoggerFactory(string logsRoot, Func<DateTimeOffset>? clock = null)
    {
        _logsRoot = logsRoot;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public IMonitoringLogger Create(DateTimeOffset startedAt) =>
        MonitoringRunLogger.Open(_logsRoot, startedAt, _clock);
}

public sealed class MonitoringRunLogger : IMonitoringLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly StreamWriter _textWriter;
    private readonly StreamWriter _jsonWriter;
    private readonly string _runId;
    private bool _disposed;

    private MonitoringRunLogger(
        string textPath,
        string jsonlPath,
        StreamWriter textWriter,
        StreamWriter jsonWriter,
        string runId)
    {
        LogFilePath = textPath;
        JsonlFilePath = jsonlPath;
        _textWriter = textWriter;
        _jsonWriter = jsonWriter;
        _runId = runId;
    }

    public string? LogFilePath { get; }
    public string? JsonlFilePath { get; }
    public string? LastError { get; private set; }
    public string RunId => _runId;

    public static MonitoringRunLogger Open(
        string logsRoot,
        DateTimeOffset startedAt,
        Func<DateTimeOffset>? clock = null)
    {
        var localStart = startedAt.ToLocalTime();
        var dayFolder = Path.Combine(logsRoot, localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dayFolder);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var baseName = $"DSMonitor_{localStart:yyyy-MM-dd_HH-mm-ss}_{suffix}";
            var textPath = Path.Combine(dayFolder, baseName + ".log");
            var jsonlPath = Path.Combine(dayFolder, baseName + ".jsonl");

            FileStream? textStream = null;
            FileStream? jsonStream = null;
            var textCreated = false;
            var jsonlCreated = false;
            try
            {
                textStream = new FileStream(
                    textPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                textCreated = true;

                jsonStream = new FileStream(
                    jsonlPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                jsonlCreated = true;

                var textWriter = new StreamWriter(textStream, new UTF8Encoding(false));
                textWriter.AutoFlush = true;

                var jsonWriter = new StreamWriter(jsonStream, new UTF8Encoding(false));
                jsonWriter.AutoFlush = true;

                return new MonitoringRunLogger(
                    textPath,
                    jsonlPath,
                    textWriter,
                    jsonWriter,
                    Guid.NewGuid().ToString("N"));
            }
            catch (IOException)
            {
                textStream?.Dispose();
                jsonStream?.Dispose();
                if (textCreated)
                {
                    TryDelete(textPath);
                }

                if (jsonlCreated)
                {
                    TryDelete(jsonlPath);
                }
            }
        }

        throw new IOException("A unique monitoring log file could not be created.");
    }

    public void RunStarted(DateTimeOffset startedAt)
    {
        Write("monitoring_started", null, null, "INFO", "Monitoring started.");
    }

    public void JobDiscovered(ForgeJobMetadata job)
    {
        Write(
            "job_discovered",
            job.JobId,
            null,
            "INFO",
            $"Forge job discovered; status {job.ExplicitStatus.ToDisplay()}.");
    }

    public void JobStatusChanged(string jobId, ForgeJobStatus previous, ForgeJobStatus current)
    {
        Write(
            "job_status_changed",
            jobId,
            null,
            "INFO",
            $"Forge job status {previous.ToDisplay()} -> {current.ToDisplay()}.");
    }

    public void SessionObserved(string jobId, string sessionId)
    {
        Write("session_observed", jobId, sessionId, "INFO", "OpenCode session id observed in forge log.");
    }

    public void EventsObserved(IReadOnlyList<MonitoringEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                foreach (var item in events)
                {
                    WriteCore(
                        "log_event",
                        item.JobId,
                        item.SessionId,
                        item.Severity.ToString().ToUpperInvariant(),
                        item.Summary,
                        item.Timestamp);
                }

                _textWriter.Flush();
                _jsonWriter.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LastError = "Monitoring log write failed.";
            }
        }
    }

    public void Warning(string summary)
    {
        Write("warning", null, null, "WARN", summary);
    }

    public void RunStopped(DateTimeOffset stoppedAt)
    {
        Write("monitoring_stopped", null, null, "INFO", "Monitoring stopped.");
    }

    private void Write(
        string kind,
        string? jobId,
        string? sessionId,
        string severity,
        string summary)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                WriteCore(kind, jobId, sessionId, severity, summary, null);
                _textWriter.Flush();
                _jsonWriter.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LastError = "Monitoring log write failed.";
            }
        }
    }

    private void WriteCore(
        string kind,
        string? jobId,
        string? sessionId,
        string severity,
        string summary,
        DateTimeOffset? eventTimestamp)
    {
        var now = DateTimeOffset.Now;
        var safeSummary = Sanitize(summary);
        var safeJobId = Sanitize(jobId);
        var safeSessionId = Sanitize(sessionId);

        var jobLabel = string.IsNullOrWhiteSpace(safeJobId) ? string.Empty : $" [{safeJobId}]";
        _textWriter.WriteLine(
            $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{severity}]{jobLabel} {safeSummary}");

        var entry = new LogEntry
        {
            Timestamp = now,
            EventTimestamp = eventTimestamp,
            RunId = _runId,
            Kind = kind,
            JobId = safeJobId,
            SessionId = safeSessionId,
            Severity = severity,
            Summary = safeSummary
        };
        _jsonWriter.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length <= 300 ? result : result[..300];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _textWriter.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Logging failures must not crash monitoring shutdown.
            }

            try
            {
                _jsonWriter.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Logging failures must not crash monitoring shutdown.
            }
        }
    }

    private sealed class LogEntry
    {
        [JsonPropertyName("ts")]
        public DateTimeOffset Timestamp { get; init; }

        [JsonPropertyName("eventTs")]
        public DateTimeOffset? EventTimestamp { get; init; }

        [JsonPropertyName("runId")]
        public string RunId { get; init; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonPropertyName("jobId")]
        public string? JobId { get; init; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }

        [JsonPropertyName("severity")]
        public string Severity { get; init; } = "INFO";

        [JsonPropertyName("summary")]
        public string Summary { get; init; } = string.Empty;
    }
}
