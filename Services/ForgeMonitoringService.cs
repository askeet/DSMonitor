using System.IO;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;

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
    private readonly Func<EventLedger>? _ledgerFactory;

    public ForgeMonitoringService(
        string runsRoot,
        IMonitoringLoggerFactory loggerFactory,
        MonitoringOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Func<EventLedger>? ledgerFactory = null)
    {
        _runsRoot = PathSafety.NormalizeRoot(runsRoot);
        _discovery = new ForgeJobDiscovery(_runsRoot);
        _loggerFactory = loggerFactory;
        _options = options ?? MonitoringOptions.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ledgerFactory = ledgerFactory;
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

        EventLedger? ledger = null;
        var ledgerRequested = _ledgerFactory is not null;
        if (_ledgerFactory is not null)
        {
            try
            {
                ledger = _ledgerFactory();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Fail closed: do not fall back to reader-local aggregates when
                // the durable projection database is invalid or unavailable.
                startupWarnings.Add(
                    "Durable ledger could not be opened; aggregate state is unavailable.");
            }
        }

        logger.RunStarted(startedAt);
        var state = new RunState(startupWarnings)
        {
            LedgerRequested = ledgerRequested,
            LedgerAvailable = ledger is not null
        };
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
                    snapshot = Scan(state, logger, _clock(), cancellationToken, ledger);
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
            ledger?.Dispose();
            logger.RunStopped(_clock());
            logger.Dispose();
        }
    }

    private MonitoringSnapshot Scan(
        RunState state,
        IMonitoringLogger logger,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        EventLedger? ledger)
    {
        if (ledger is not null)
        {
            return ScanDurable(state, logger, observedAt, cancellationToken, ledger);
        }

        return state.LedgerRequested
            ? ScanMetadataOnly(state, logger, observedAt, cancellationToken)
            : ScanLegacy(state, logger, observedAt, cancellationToken);
    }

    private MonitoringSnapshot ScanLegacy(
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

                if (!state.TimelineBuffers.ContainsKey(metadata.JobId))
                {
                    state.TimelineBuffers[metadata.JobId] = new TimelineRetentionBuffer(
                        _options.MaxRecentEventsPerJob);
                }

                if (!state.SeenEventKeys.ContainsKey(metadata.JobId))
                {
                    state.SeenEventKeys[metadata.JobId] = new HashSet<string>(StringComparer.Ordinal);
                    state.SeenKeyOrder[metadata.JobId] = new Queue<string>();
                }

                if (!state.AcceptedEventCounts.ContainsKey(metadata.JobId))
                {
                    state.AcceptedEventCounts[metadata.JobId] = 0;
                }

                if (!state.PreviousStatuses.ContainsKey(metadata.JobId))
                {
                    state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
                    logger.JobDiscovered(metadata);
                }
            }

            var timeline = state.TimelineBuffers[metadata.JobId];
            var seen = state.SeenEventKeys[metadata.JobId];
            var seenOrder = state.SeenKeyOrder[metadata.JobId];

            // The 10,000-key recent window is bounded, so an old key can be
            // evicted while its high-timestamp event is still retained. The
            // reader evaluates this predicate before updating cost, session,
            // activity, or accepted-count aggregates, so a rejected replay has
            // no projected effect at all. The per-poll set makes the decision
            // consistent even if the reader's own bounded window evicts a key
            // before an identical line later in the same poll.
            var acceptedThisPoll = new HashSet<string>(StringComparer.Ordinal);
            var poll = reader.Poll(
                observedAt,
                observedEvent =>
                    !acceptedThisPoll.Contains(observedEvent.EventKey) &&
                    !seen.Contains(observedEvent.EventKey) &&
                    !timeline.ContainsRetainedKey(observedEvent.EventKey) &&
                    acceptedThisPoll.Add(observedEvent.EventKey));
            foreach (var warning in poll.Warnings)
            {
                if (!globalWarnings.Contains(warning, StringComparer.Ordinal))
                {
                    globalWarnings.Add(warning);
                }
            }

            var acceptedEventCount = state.AcceptedEventCounts[metadata.JobId];
            var newlyAccepted = new List<MonitoringEvent>();
            foreach (var observedEvent in poll.NewEvents)
            {
                // Reader-local dedupe guarantees one occurrence per key in a
                // poll; this guard keeps the service acceptance set consistent
                // if that invariant is ever changed.
                if (!seen.Add(observedEvent.EventKey))
                {
                    continue;
                }

                seenOrder.Enqueue(observedEvent.EventKey);
                while (seenOrder.Count > 10_000)
                {
                    seen.Remove(seenOrder.Dequeue());
                }

                acceptedEventCount++;
                newlyAccepted.Add(observedEvent);
                eventsToLog.Add(observedEvent);
            }

            state.AcceptedEventCounts[metadata.JobId] = acceptedEventCount;

            if (newlyAccepted.Count > 0)
            {
                timeline.AddRange(newlyAccepted);
            }

            var hasObservedAmbiguity = timeline.HasObservedAmbiguousTimestamps;

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
                ParsedEventCount = state.AcceptedEventCounts[metadata.JobId],
                ObservedCost = reader.HasCost ? reader.TotalCost : null,
                HasObservedAmbiguousTimestamps = hasObservedAmbiguity,
                Events = timeline.RetainSourceOrder(),
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
            state.TimelineBuffers.Remove(staleId);
            state.PreviousStatuses.Remove(staleId);
            state.LastSessions.Remove(staleId);
            state.SeenEventKeys.Remove(staleId);
            state.SeenKeyOrder.Remove(staleId);
            state.AcceptedEventCounts.Remove(staleId);
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

    private MonitoringSnapshot ScanDurable(
        RunState state,
        IMonitoringLogger logger,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        EventLedger ledger)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var discovery = _discovery.Discover();
        var activityPreflight = JobLiveActivityReader.Preflight(
            discovery.Jobs, _runsRoot, cancellationToken);
        var jobs = new List<ForgeJobSnapshot>();
        var globalWarnings = new List<string>(state.StartupWarnings);
        globalWarnings.AddRange(discovery.Warnings);
        var eventsToLog = new List<MonitoringEvent>();
        var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ledger.UpdateDiscoveryState(
                _runsRoot,
                discovery.DirectoryAvailable,
                discovery.Warnings,
                observedAt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            globalWarnings.Add("Discovery coverage could not be persisted.");
        }

        foreach (var metadata in discovery.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discoveredIds.Add(metadata.JobId);

            var registrationConflict = false;
            try
            {
                registrationConflict = ledger.RegisterJob(
                    metadata.JobId,
                    _runsRoot,
                    metadata.MetadataPath,
                    observedAt,
                    metadata.IsProvisional);
                if (registrationConflict)
                {
                    globalWarnings.Add(
                        $"Durable job '{metadata.JobId}' has conflicting runs-root or metadata associations; ingestion was refused.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                registrationConflict = true;
                globalWarnings.Add(
                    $"Durable job registration failed for '{metadata.JobId}'.");
            }

            if (!state.RestoredJobs.Contains(metadata.JobId))
            {
                try
                {
                    var aggregate = ledger.GetJobAggregate(metadata.JobId);
                    var restoredTimeline = new TimelineRetentionBuffer(
                        _options.MaxRecentEventsPerJob);
                    var timelineSnapshot = ledger.ListTimelineSnapshot(
                        metadata.JobId,
                        Math.Clamp(
                            _options.MaxRecentEventsPerJob,
                            1,
                            EventLedger.MaxReadLimit));
                    restoredTimeline.Restore(
                        timelineSnapshot.SourceWindow,
                        timelineSnapshot.NewestTimestampedWindow,
                        aggregate.TimelineAmbiguous ||
                        timelineSnapshot.HasObservedAmbiguousTimestamps);
                    state.TimelineBuffers[metadata.JobId] = restoredTimeline;
                    state.LastSessions[metadata.JobId] =
                        aggregate.LatestSessionId ?? string.Empty;
                    state.AcceptedEventCounts[metadata.JobId] =
                        checked((int)Math.Min(aggregate.LogicalEventCount, int.MaxValue));
                    state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
                    state.RestoredJobs.Add(metadata.JobId);
                    logger.JobDiscovered(metadata);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    globalWarnings.Add(
                        $"Durable aggregate restore failed for '{metadata.JobId}'.");
                    continue;
                }
            }

            var timeline = state.TimelineBuffers[metadata.JobId];
            var primaryPath = metadata.LogPath ??
                Path.Combine(_runsRoot, metadata.JobId + ".log");
            var jobWarnings = new List<string>(metadata.Warnings);
            var sourcePaths = new List<string> { primaryPath };
            try
            {
                sourcePaths.AddRange(
                    ledger.ListJobSources(metadata.JobId)
                        .Select(source => source.SourcePath));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                jobWarnings.Add("Previously registered source paths could not be read.");
            }

            var distinctSources = sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (registrationConflict)
            {
                jobWarnings.Add(
                    "Durable job identity is conflicting; no source was ingested and committed aggregates were retained.");
                distinctSources = Array.Empty<string>();
            }

            if (!state.Readers.TryGetValue(metadata.JobId, out var primaryReader) ||
                !string.Equals(
                    primaryReader.LogPath,
                    primaryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                primaryReader = new ForgeLogTailReader(
                    metadata.JobId,
                    primaryPath,
                    _runsRoot,
                    _options.MaxBytesPerPoll,
                    _options.MaxLogLineBytes);
                state.Readers[metadata.JobId] = primaryReader;
            }

            LedgerSourceObservation? primarySourceObservation = null;
            foreach (var sourcePath in distinctSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isPrimary = string.Equals(
                    sourcePath,
                    primaryPath,
                    StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (isPrimary)
                    {
                        ledger.RegisterJobSource(
                            metadata.JobId,
                            sourcePath,
                            "Registered",
                            observedAt);
                    }

                    var result = string.Equals(
                        sourcePath,
                        primaryPath,
                        StringComparison.OrdinalIgnoreCase)
                        ? primaryReader.PollDurable(ledger, observedAt, cancellationToken)
                        : ledger.Ingest(
                            metadata.JobId,
                            sourcePath,
                            _runsRoot,
                            observedAt,
                            cancellationToken);

                    foreach (var warning in result.Warnings)
                    {
                        if (!jobWarnings.Contains(warning, StringComparer.Ordinal))
                        {
                            jobWarnings.Add(warning);
                        }
                    }

                    if (result.Status == LedgerIngestStatus.SourceUnavailable)
                    {
                        if (isPrimary)
                        {
                            jobWarnings.Add(
                                "The primary job log is unavailable; committed aggregates and coverage are retained.");
                        }
                        else
                        {
                            jobWarnings.Add(
                                "A previously registered source is unavailable; coverage is incomplete.");
                        }

                        TryMarkSourceUnavailable(
                            ledger,
                            metadata.JobId,
                            sourcePath,
                            observedAt);
                        continue;
                    }

                    if (!result.IsSuccess)
                    {
                        var coverageStatus = result.Status switch
                        {
                            LedgerIngestStatus.RecordTooLarge =>
                                LedgerCoverageState.Malformed.ToString(),
                            LedgerIngestStatus.ContinuityUncertain or
                            LedgerIngestStatus.AmbiguousSourceIdentity or
                            LedgerIngestStatus.IdentityConflict =>
                                LedgerCoverageState.Conflict.ToString(),
                            _ => LedgerCoverageState.Unavailable.ToString()
                        };

                        if (result.Status == LedgerIngestStatus.RecordTooLarge)
                        {
                            jobWarnings.Add(
                                "A source record exceeded the bounded ingestion window; coverage is incomplete.");
                        }

                        TryMarkSourceState(
                            ledger,
                            metadata.JobId,
                            sourcePath,
                            coverageStatus,
                            observedAt);
                        continue;
                    }

                    if (isPrimary)
                    {
                        primarySourceObservation = result.SourceObservation;
                    }

                    if (result.AcceptedEvents.Count > 0)
                    {
                        timeline.AddRange(result.AcceptedEvents);
                        eventsToLog.AddRange(result.AcceptedEvents);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (LedgerException ex)
                {
                    jobWarnings.Add(
                        $"Durable ledger rejected the batch for '{metadata.JobId}': {ex.Message}");
                    var conflict = ex.Failure is
                        LedgerStorageFailure.ConstraintViolation or
                        LedgerStorageFailure.NumericOverflow;
                    TryMarkSourceState(
                        ledger,
                        metadata.JobId,
                        sourcePath,
                        conflict
                            ? LedgerCoverageState.Conflict.ToString()
                            : LedgerCoverageState.Unavailable.ToString(),
                        observedAt);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    jobWarnings.Add(
                        $"Durable ingestion failed for '{metadata.JobId}'; committed state was retained.");
                    TryMarkSourceUnavailable(
                        ledger,
                        metadata.JobId,
                        sourcePath,
                        observedAt);
                }
            }

            string? uncoveredDetail = null;
            if (!registrationConflict && PathSafety.IsSafeFileName(metadata.JobId))
            {
                var fallbackPath = Path.Combine(_runsRoot, metadata.JobId + ".log");
                var fallbackIsProcessed = distinctSources.Any(path =>
                    string.Equals(path, fallbackPath, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(fallbackPath, primaryPath, StringComparison.OrdinalIgnoreCase) &&
                    !fallbackIsProcessed &&
                    File.Exists(fallbackPath))
                {
                    uncoveredDetail =
                        $"An eligible fallback log '{Path.GetFileName(fallbackPath)}' exists but is not associated with the metadata log path; it was not ingested, so coverage is incomplete.";
                    if (!jobWarnings.Contains(uncoveredDetail, StringComparer.Ordinal))
                    {
                        jobWarnings.Add(uncoveredDetail);
                    }
                }
            }

            try
            {
                ledger.SetJobCoverageIncomplete(
                    metadata.JobId,
                    uncoveredDetail,
                    observedAt);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                jobWarnings.Add("Uncovered-source coverage could not be persisted.");
            }

            LedgerJobAggregate committedAggregate;
            LedgerCoverage coverage;
            try
            {
                committedAggregate = ledger.GetJobAggregate(metadata.JobId);
                coverage = ledger.GetJobCoverage(metadata.JobId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                jobWarnings.Add("Committed durable aggregates could not be read.");
                continue;
            }

            if (coverage.State != LedgerCoverageState.Clean)
            {
                if (!jobWarnings.Contains(coverage.Detail, StringComparer.Ordinal))
                {
                    jobWarnings.Add(coverage.Detail);
                }
            }

            state.AcceptedEventCounts[metadata.JobId] =
                checked((int)Math.Min(committedAggregate.LogicalEventCount, int.MaxValue));

            if (state.PreviousStatuses.TryGetValue(metadata.JobId, out var previousStatus) &&
                previousStatus != metadata.ExplicitStatus)
            {
                logger.JobStatusChanged(
                    metadata.JobId,
                    previousStatus,
                    metadata.ExplicitStatus);
                state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
            }

            if (!string.IsNullOrWhiteSpace(committedAggregate.LatestSessionId) &&
                (!state.LastSessions.TryGetValue(metadata.JobId, out var previousSession) ||
                 !string.Equals(
                     previousSession,
                     committedAggregate.LatestSessionId,
                     StringComparison.Ordinal)))
            {
                state.LastSessions[metadata.JobId] =
                    committedAggregate.LatestSessionId ?? string.Empty;
                logger.SessionObserved(
                    metadata.JobId,
                    committedAggregate.LatestSessionId!);
            }

            jobs.Add(new ForgeJobSnapshot
            {
                Metadata = metadata,
                Status = metadata.ExplicitStatus,
                SessionId = committedAggregate.LatestSessionId,
                LastActivity = committedAggregate.LastActivity,
                ParsedEventCount = state.AcceptedEventCounts[metadata.JobId],
                ObservedCost = committedAggregate.HasCost
                    ? (double)committedAggregate.CostTotal
                    : null,
                HasIncompleteCoverage = coverage.State != LedgerCoverageState.Clean,
                HasObservedAmbiguousTimestamps = committedAggregate.TimelineAmbiguous,
                LiveActivity = !registrationConflict &&
                               primarySourceObservation is { } primaryObservation &&
                               coverage.State != LedgerCoverageState.Conflict &&
                               activityPreflight.TryGetValue(metadata.JobId, out var activityEvidence)
                    ? JobLiveActivityReader.Read(
                        metadata,
                        activityEvidence,
                        primaryObservation,
                        _runsRoot,
                        observedAt,
                        cancellationToken,
                        _options.MaxBytesPerPoll)
                    : new LiveActivitySnapshot
                    {
                        Availability = "N/A — durable source unavailable or conflicting; no cached excerpt is shown."
                    },
                Events = timeline.RetainSourceOrder(),
                Warnings = jobWarnings.Distinct(StringComparer.Ordinal).ToArray(),
                ObservedAt = observedAt
            });
        }

        var staleIds = state.RestoredJobs
            .Where(jobId => !discoveredIds.Contains(jobId))
            .ToArray();
        foreach (var staleId in staleIds)
        {
            state.RestoredJobs.Remove(staleId);
            state.TimelineBuffers.Remove(staleId);
            state.PreviousStatuses.Remove(staleId);
            state.LastSessions.Remove(staleId);
            state.AcceptedEventCounts.Remove(staleId);
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

    private static void TryMarkSourceUnavailable(
        EventLedger ledger,
        string jobId,
        string sourcePath,
        DateTimeOffset observedAt) =>
        TryMarkSourceState(
            ledger,
            jobId,
            sourcePath,
            LedgerCoverageState.Unavailable.ToString(),
            observedAt);

    private static void TryMarkSourceState(
        EventLedger ledger,
        string jobId,
        string sourcePath,
        string status,
        DateTimeOffset observedAt)
    {
        try
        {
            ledger.RegisterJobSource(jobId, sourcePath, status, observedAt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Coverage marking is best effort; the original failure is reported.
        }
    }

    private MonitoringSnapshot ScanMetadataOnly(
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
        var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metadata in discovery.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discoveredIds.Add(metadata.JobId);
            if (!state.PreviousStatuses.ContainsKey(metadata.JobId))
            {
                state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
                logger.JobDiscovered(metadata);
            }
            else if (state.PreviousStatuses[metadata.JobId] != metadata.ExplicitStatus)
            {
                logger.JobStatusChanged(
                    metadata.JobId,
                    state.PreviousStatuses[metadata.JobId],
                    metadata.ExplicitStatus);
                state.PreviousStatuses[metadata.JobId] = metadata.ExplicitStatus;
            }

            var warnings = metadata.Warnings
                .Append("Durable aggregate state is unavailable.")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            jobs.Add(new ForgeJobSnapshot
            {
                Metadata = metadata,
                Status = metadata.ExplicitStatus,
                ObservedAt = observedAt,
                Warnings = warnings
            });
        }

        foreach (var staleId in state.PreviousStatuses.Keys
                     .Where(jobId => !discoveredIds.Contains(jobId))
                     .ToArray())
        {
            state.PreviousStatuses.Remove(staleId);
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
        public bool LedgerRequested { get; set; }
        public bool LedgerAvailable { get; set; }
        public HashSet<string> RestoredJobs { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ForgeLogTailReader> Readers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TimelineRetentionBuffer> TimelineBuffers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> SeenEventKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Queue<string>> SeenKeyOrder { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> AcceptedEventCounts { get; } =
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
