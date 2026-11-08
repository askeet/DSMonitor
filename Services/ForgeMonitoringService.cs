using System.IO;
using DSMonitor.Models;
using DSMonitor.Parsing;

namespace DSMonitor.Services;

/// <summary>
/// Reconciles forge metadata and tails job logs on a cancellable periodic
/// loop. FileSystemWatcher events wake the loop early; reconciliation still
/// runs periodically so missed watcher events are recovered.
/// </summary>
public sealed class ForgeMonitoringService
{
    private readonly string _runsRoot;
    private readonly ForgeJobDiscovery _discovery;
    private readonly IMonitoringLoggerFactory _loggerFactory;
    private readonly MonitoringOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public ForgeMonitoringService(
        string runsRoot,
        IMonitoringLoggerFactory loggerFactory,
        MonitoringOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        _runsRoot = PathSafety.NormalizeRoot(runsRoot);
        _discovery = new ForgeJobDiscovery(_runsRoot);
        _loggerFactory = loggerFactory;
        _options = options ?? MonitoringOptions.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string RunsRoot => _runsRoot;
    public TimeSpan RefreshInterval => _options.RefreshInterval;

    public async Task RunAsync(
        Func<MonitoringSnapshot, Task> onSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSnapshot);

        var startedAt = _clock();
        var startupWarnings = new List<string>();
        IMonitoringLogger logger;
        try
        {
            logger = _loggerFactory.Create(startedAt);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException)
        {
            logger = NullMonitoringLogger.Instance;
            startupWarnings.Add("Monitoring log could not be created; monitoring continues.");
        }

        logger.RunStarted(startedAt);
        var state = new RunState(startupWarnings);
        FileSystemWatcher? watcher = null;
        using var signal = new SemaphoreSlim(0, 1);

        void Signal()
        {
            try
            {
                if (signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // A rescan is already pending.
            }
        }

        void EnsureWatcher()
        {
            if (!Directory.Exists(_runsRoot))
            {
                if (watcher is not null)
                {
                    watcher.Dispose();
                    watcher = null;
                }

                return;
            }

            if (watcher is not null)
            {
                return;
            }

            try
            {
                watcher = new FileSystemWatcher(_runsRoot)
                {
                    IncludeSubdirectories = false,
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size |
                                   NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, _) => Signal();
                watcher.Changed += (_, _) => Signal();
                watcher.Deleted += (_, _) => Signal();
                watcher.Renamed += (_, _) => Signal();
                watcher.Error += (_, _) => Signal();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                watcher = null;
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EnsureWatcher();

                MonitoringSnapshot snapshot;
                try
                {
                    snapshot = Scan(state, logger, _clock(), cancellationToken);
                    if (logger.LastError is { } loggerError)
                    {
                        snapshot = snapshot with
                        {
                            Warnings = snapshot.Warnings
                                .Append(loggerError)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray()
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.Warning("A monitoring scan failed; showing the last known data.");
                    snapshot = new MonitoringSnapshot
                    {
                        ObservedAt = _clock(),
                        State = MonitoringState.Error,
                        ForgeDirectoryAvailable = Directory.Exists(_runsRoot),
                        Jobs = state.LastJobs.Values
                            .OrderByDescending(job => job.Metadata.Started ?? DateTimeOffset.MinValue)
                            .ToArray(),
                        Warnings = state.LastWarnings
                            .Append("Monitoring scan failed; showing the last known data.")
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        Error = "Monitoring scan failed."
                    };
                }

                foreach (var warning in snapshot.Warnings)
                {
                    if (state.ReportedWarnings.Add(warning))
                    {
                        logger.Warning(warning);
                    }
                }

                try
                {
                    await onSnapshot(snapshot).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.Warning("A dashboard update could not be applied.");
                }

                try
                {
                    await signal.WaitAsync(_options.RefreshInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            watcher?.Dispose();
            logger.RunStopped(_clock());
            logger.Dispose();
        }
    }

    private MonitoringSnapshot Scan(
        RunState state,
        IMonitoringLogger logger,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var discovery = _discovery.Discover();
        var jobs = new List<ForgeJobSnapshot>();
        var globalWarnings = new List<string>(state.StartupWarnings);
        globalWarnings.AddRange(discovery.Warnings);
        var eventsToLog = new List<MonitoringEvent>();
        var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metadata in discovery.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discoveredIds.Add(metadata.JobId);

            if (!state.Readers.TryGetValue(metadata.JobId, out var reader) ||
                !string.Equals(reader.LogPath, metadata.LogPath, StringComparison.OrdinalIgnoreCase))
            {
                reader = new ForgeLogTailReader(
                    metadata.JobId,
                    metadata.LogPath ?? Path.Combine(_runsRoot, metadata.JobId + ".log"),
                    _runsRoot,
                    _options.MaxBytesPerPoll,
                    _options.MaxLogLineBytes);
                state.Readers[metadata.JobId] = reader;

                if (!state.RecentEvents.ContainsKey(metadata.JobId))
                {
                    state.RecentEvents[metadata.JobId] = new List<MonitoringEvent>();
                }

                if (!state.SeenEventKeys.ContainsKey(metadata.JobId))
                {
                    state.SeenEventKeys[metadata.JobId] = new HashSet<string>(StringComparer.Ordinal);
                    state.SeenKeyOrder[metadata.JobId] = new Queue<string>();
                }

                if (!state.PreviousStatuses.ContainsKey(metadata.JobId))
                {
                    state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
                    logger.JobDiscovered(metadata);
                }
            }

            var poll = reader.Poll(observedAt);
            foreach (var warning in poll.Warnings)
            {
                if (!globalWarnings.Contains(warning, StringComparer.Ordinal))
                {
                    globalWarnings.Add(warning);
                }
            }

            var recent = state.RecentEvents[metadata.JobId];
            var seen = state.SeenEventKeys[metadata.JobId];
            var seenOrder = state.SeenKeyOrder[metadata.JobId];
            foreach (var observedEvent in poll.NewEvents)
            {
                if (seen.Add(observedEvent.EventKey))
                {
                    seenOrder.Enqueue(observedEvent.EventKey);
                    while (seenOrder.Count > 10_000)
                    {
                        seen.Remove(seenOrder.Dequeue());
                    }

                    recent.Add(observedEvent);
                    eventsToLog.Add(observedEvent);
                }
            }

            var hasObservedAmbiguity =
                state.AmbiguousTimelineJobs.Contains(metadata.JobId) ||
                recent.Any(item => item.Timestamp is null);
            if (hasObservedAmbiguity)
            {
                state.AmbiguousTimelineJobs.Add(metadata.JobId);
            }

            if (recent.Count > _options.MaxRecentEventsPerJob)
            {
                var retained = TimelineOrdering.RetainSourceOrder(
                    recent,
                    _options.MaxRecentEventsPerJob,
                    hasObservedAmbiguity);
                recent.Clear();
                recent.AddRange(retained);
            }

            if (state.PreviousStatuses.TryGetValue(metadata.JobId, out var previousStatus) &&
                previousStatus != metadata.ExplicitStatus)
            {
                logger.JobStatusChanged(metadata.JobId, previousStatus, metadata.ExplicitStatus);
                state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
            }

            var sessionId = reader.LatestSessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                if (!state.LastSessions.TryGetValue(metadata.JobId, out var previousSession) ||
                    !string.Equals(previousSession, sessionId, StringComparison.Ordinal))
                {
                    state.LastSessions[metadata.JobId] = sessionId;
                    logger.SessionObserved(metadata.JobId, sessionId);
                }
            }

            var jobWarnings = metadata.Warnings
                .Concat(poll.Warnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            jobs.Add(new ForgeJobSnapshot
            {
                Metadata = metadata,
                Status = metadata.ExplicitStatus,
                SessionId = sessionId,
                LastActivity = reader.LastActivity,
                ParsedEventCount = reader.TotalUniqueEvents,
                ObservedCost = reader.HasCost ? reader.TotalCost : null,
                HasObservedAmbiguousTimestamps = hasObservedAmbiguity,
                Events = recent.ToArray(),
                Warnings = jobWarnings,
                ObservedAt = observedAt
            });
        }

        var staleIds = state.Readers.Keys
            .Where(jobId => !discoveredIds.Contains(jobId))
            .ToArray();
        foreach (var staleId in staleIds)
        {
            state.Readers.Remove(staleId);
            state.RecentEvents.Remove(staleId);
            state.PreviousStatuses.Remove(staleId);
            state.LastSessions.Remove(staleId);
            state.SeenEventKeys.Remove(staleId);
            state.SeenKeyOrder.Remove(staleId);
            state.AmbiguousTimelineJobs.Remove(staleId);
        }

        if (eventsToLog.Count > 0)
        {
            logger.EventsObserved(eventsToLog);
        }

        var snapshot = new MonitoringSnapshot
        {
            ObservedAt = observedAt,
            State = jobs.Any(job => job.Status.IsActive())
                ? MonitoringState.Monitoring
                : MonitoringState.Idle,
            ForgeDirectoryAvailable = discovery.DirectoryAvailable,
            Jobs = jobs,
            Warnings = globalWarnings.Distinct(StringComparer.Ordinal).ToArray()
        };

        state.LastJobs = jobs.ToDictionary(job => job.JobId, StringComparer.OrdinalIgnoreCase);
        state.LastWarnings = snapshot.Warnings;
        return snapshot;
    }

    private sealed class RunState
    {
        public RunState(IEnumerable<string> startupWarnings)
        {
            StartupWarnings = startupWarnings.ToArray();
            LastWarnings = StartupWarnings;
        }

        public IReadOnlyList<string> StartupWarnings { get; }
        public Dictionary<string, ForgeLogTailReader> Readers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<MonitoringEvent>> RecentEvents { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> SeenEventKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Queue<string>> SeenKeyOrder { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousTimelineJobs { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ForgeJobStatus> PreviousStatuses { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LastSessions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReportedWarnings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ForgeJobSnapshot> LastJobs { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> LastWarnings { get; set; }
    }
}
