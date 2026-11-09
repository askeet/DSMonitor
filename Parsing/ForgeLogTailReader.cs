using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Parsing;

public sealed record TailPollResult(
    IReadOnlyList<MonitoringEvent> NewEvents,
    bool ResetDetected,
    bool LogMissing,
    int MalformedLinesThisPoll,
    long BytesRead,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Incremental, bounded reader for one forge job log. Handles file growth,
/// partial lines, malformed lines, truncation and replacement. It never
/// holds an exclusive lock on forge-owned files.
/// </summary>
public sealed class ForgeLogTailReader
{
    private const int MaxSeenEventKeys = 10_000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly string _runsRoot;
    private readonly long _maxBytesPerPoll;
    private readonly long _maxLineBytes;
    private readonly byte[] _byteBuffer;
    private readonly char[] _charBuffer;
    private readonly StringBuilder _pending = new();
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenKeyOrder = new();

    private Decoder _decoder = Utf8.GetDecoder();
    private long _offset;
    private FileIdentity? _identity;
    private bool _discardingOverlongLine;

    public ForgeLogTailReader(
        string jobId,
        string logPath,
        string runsRoot,
        long maxBytesPerPoll = 512 * 1024,
        long maxLineBytes = 1024 * 1024)
    {
        JobId = jobId;
        LogPath = logPath;
        _runsRoot = runsRoot;
        _maxBytesPerPoll = Math.Max(4096, maxBytesPerPoll);
        _maxLineBytes = Math.Max(4096, maxLineBytes);
        _byteBuffer = new byte[(int)Math.Min(_maxBytesPerPoll, 256 * 1024)];
        _charBuffer = new char[Utf8.GetMaxCharCount(_byteBuffer.Length)];
        IsPathAllowed = PathSafety.IsWithinRoot(logPath, runsRoot);
    }

    public string JobId { get; }
    public string LogPath { get; }
    public bool IsPathAllowed { get; }
    public long Offset => _offset;
    public int MalformedLineCount { get; private set; }
    public int TotalUniqueEvents { get; private set; }
    public double TotalCost { get; private set; }
    public bool HasCost { get; private set; }
    public string? LatestSessionId { get; private set; }
    public DateTimeOffset? LastActivity { get; private set; }
    public int ResetCount { get; private set; }
    public bool IsMissing { get; private set; }

    /// <summary>
    /// Incrementally reads newly appended complete lines. When
    /// <paramref name="acceptEvent"/> is supplied, it is evaluated after the
    /// reader's own bounded dedupe and before any cost, session, activity, or
    /// accepted-count aggregate is updated. Returning false discards the event
    /// without any aggregate effect or inclusion in <see cref="TailPollResult.NewEvents"/>.
    /// </summary>
    public TailPollResult Poll(
        DateTimeOffset observedAt,
        Func<MonitoringEvent, bool>? acceptEvent = null)
    {
        var newEvents = new List<MonitoringEvent>();
        var warnings = new List<string>();

        using var opened = SafeFileAccess.OpenRead(LogPath, _runsRoot);
        if (opened.Stream is null)
        {
            IsMissing = opened.Failure is
                SafeReadFailure.Missing or
                SafeReadFailure.Unavailable or
                SafeReadFailure.OutsideRoot;
            warnings.Add(opened.Warning ?? "Job log could not be read safely.");
            return new TailPollResult(
                newEvents,
                false,
                IsMissing,
                0,
                0,
                warnings);
        }

        IsMissing = false;

        try
        {
            var stream = opened.Stream!;

            var identity = FileIdentity.Read(stream, LogPath);
            if (_identity is { } previousIdentity && !previousIdentity.Equals(identity))
            {
                ResetState();
                warnings.Add("Job log was replaced; reading from the beginning.");
            }

            _identity = identity;
            var length = stream.Length;
            if (length < _offset)
            {
                ResetState();
                warnings.Add("Job log was truncated; reading from the beginning.");
            }

            long bytesRead = 0;
            var malformedBefore = MalformedLineCount;

            if (length > _offset)
            {
                var available = length - _offset;
                var requested = (int)Math.Min(_byteBuffer.Length, Math.Min(available, _maxBytesPerPoll));
                stream.Position = _offset;
                var read = stream.Read(_byteBuffer, 0, requested);
                if (read > 0)
                {
                    _offset += read;
                    bytesRead = read;
                    var charCount = _decoder.GetChars(_byteBuffer, 0, read, _charBuffer, 0, flush: false);
                    _pending.Append(_charBuffer, 0, charCount);
                    ProcessBufferedLines(newEvents, observedAt, warnings, acceptEvent);
                }
            }

            var malformedThisPoll = MalformedLineCount - malformedBefore;
            if (malformedThisPoll > 0)
            {
                warnings.Add($"{malformedThisPoll} malformed log line(s) were skipped.");
            }

            return new TailPollResult(
                newEvents,
                warnings.Any(w => w.Contains("replaced", StringComparison.Ordinal) ||
                                  w.Contains("truncated", StringComparison.Ordinal)),
                false,
                malformedThisPoll,
                bytesRead,
                warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            IsMissing = true;
            warnings.Add("Job log is currently unavailable or locked.");
            return new TailPollResult(newEvents, false, true, 0, 0, warnings);
        }
    }

    /// <summary>
    /// Reads a bounded, runtime-only tail preview. This is never persisted by
    /// DSMonitor's own logger.
    /// </summary>
    public static string? ReadTailPreview(string? logPath, string runsRoot, int maxBytes = 64 * 1024)
    {
        using var opened = SafeFileAccess.OpenRead(logPath, runsRoot);
        if (opened.Stream is null)
        {
            return null;
        }

        try
        {
            var stream = opened.Stream!;

            if (stream.Length == 0)
            {
                return string.Empty;
            }

            var boundedMax = Math.Clamp(maxBytes, 1024, 256 * 1024);
            var start = Math.Max(0, stream.Length - boundedMax);
            var count = (int)Math.Min(stream.Length - start, boundedMax);
            var buffer = new byte[count];
            stream.Position = start;
            var read = stream.Read(buffer, 0, count);
            var text = Utf8.GetString(buffer, 0, read);

            if (start > 0)
            {
                var firstNewLine = text.IndexOf('\n');
                if (firstNewLine >= 0 && firstNewLine + 1 < text.Length)
                {
                    text = text[(firstNewLine + 1)..];
                }
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void ProcessBufferedLines(
        List<MonitoringEvent> newEvents,
        DateTimeOffset observedAt,
        List<string> warnings,
        Func<MonitoringEvent, bool>? acceptEvent)
    {
        var text = _pending.ToString();
        var consumed = 0;

        if (_discardingOverlongLine)
        {
            var overlongEnd = text.IndexOf('\n');
            if (overlongEnd < 0)
            {
                _pending.Clear();
                return;
            }

            consumed = overlongEnd + 1;
            _discardingOverlongLine = false;
        }

        var lineStart = consumed;
        while (lineStart < text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            var line = text[lineStart..newline].TrimEnd('\r');
            lineStart = newline + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > _maxLineBytes)
            {
                MalformedLineCount++;
                continue;
            }

            if (!ForgeEventParser.TryParse(line, JobId, observedAt, out var parsed) || parsed is null)
            {
                MalformedLineCount++;
                continue;
            }

            if (!_seenKeys.Add(parsed.EventKey))
            {
                continue;
            }

            _seenKeyOrder.Enqueue(parsed.EventKey);
            while (_seenKeyOrder.Count > MaxSeenEventKeys)
            {
                _seenKeys.Remove(_seenKeyOrder.Dequeue());
            }

            // Acceptance is evaluated before any projected aggregate is
            // mutated. A duplicate rejected by the caller must not add cost,
            // replace the latest session, advance activity, or increment the
            // accepted unique-event count.
            if (acceptEvent is not null && !acceptEvent(parsed.Event))
            {
                continue;
            }

            TotalUniqueEvents++;
            if (parsed.Cost is { } cost)
            {
                TotalCost += cost;
                HasCost = true;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Event.SessionId))
            {
                LatestSessionId = parsed.Event.SessionId;
            }

            var activityTime = parsed.Event.Timestamp ?? observedAt;
            if (LastActivity is null || activityTime > LastActivity)
            {
                LastActivity = activityTime;
            }

            newEvents.Add(parsed.Event);
        }

        if (lineStart > 0)
        {
            _pending.Remove(0, lineStart);
        }

        if (_pending.Length > _maxLineBytes)
        {
            _pending.Clear();
            _discardingOverlongLine = true;
            MalformedLineCount++;
            warnings.Add("An overlong log line was discarded.");
        }
    }

    private void ResetState()
    {
        ResetCount++;
        _offset = 0;
        _decoder = Utf8.GetDecoder();
        _pending.Clear();
        _discardingOverlongLine = false;
        IsMissing = false;
    }

    private readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, long CreationTicks)
    {
        public static FileIdentity Read(FileStream stream, string path)
        {
            try
            {
                if (GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                {
                    var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                    return new FileIdentity(info.VolumeSerialNumber, index, 0);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Fall through to the portable identity below.
            }

            try
            {
                return new FileIdentity(0, 0, File.GetCreationTimeUtc(path).Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileIdentity(0, 0, 0);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);
}
