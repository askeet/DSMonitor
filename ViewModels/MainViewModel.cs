using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;

namespace DSMonitor.ViewModels;

/// <summary>
/// Dashboard state and lifecycle. Monitoring work runs off the UI thread;
/// snapshots are applied on the WPF dispatcher.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Func<ForgeMonitoringService> _serviceFactory;
    private readonly Dispatcher _dispatcher;
    private readonly string _runsRoot;
    private readonly DispatcherTimer _clockTimer;
    private readonly IOriginalRequestService _originalRequestService;
    private readonly Dictionary<string, JobRowViewModel> _jobsById =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private CancellationTokenSource? _rawLogCts;
    private CancellationTokenSource? _selectedActivityCts;
    private CancellationTokenSource? _originalRequestCts;
    private bool _disposed;

    private MonitoringState _monitoringState = MonitoringState.Stopped;
    private bool _isMonitoring;
    private int _activeCount;
    private int _completedCount;
    private int _failedCount;
    private int _totalCount;
    private int _unknownCount;
    private string _unknownDetailText = "0 unknown";
    private string _costText = "—";
    private string _costDetailText = "Not reported by forge metadata";
    private string _lastRefreshText = "—";
    private string _lastSuccessfulObservationText = "—";
    private string _currentTimeText = string.Empty;
    private string _forgeDirectoryText = "Not checked";
    private string _warningsText = "No monitoring errors.";
    private bool _hasWarnings;
    private string _searchText = string.Empty;
    private JobRowViewModel? _selectedJob;
    private string _stateDetail = "Monitoring is stopped";
    private string _jobsSummaryText = "0 observed · 0 unknown";
    private string _openCodeStateText = "Not connected · Phase 2";
    private string _refreshIntervalText = "1 s";

    public MainViewModel(
        Func<ForgeMonitoringService> serviceFactory,
        Dispatcher dispatcher,
        string runsRoot,
        TimeSpan? refreshInterval = null,
        IOriginalRequestService? originalRequestService = null)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runsRoot = runsRoot;
        _originalRequestService =
            originalRequestService ?? DisabledOriginalRequestService.Instance;

        Jobs = new ObservableCollection<JobRowViewModel>();
        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        JobsView.Filter = FilterJob;
        JobsView.SortDescriptions.Add(
            new SortDescription(nameof(JobRowViewModel.StartedSort), ListSortDirection.Descending));

        StartCommand = new AsyncRelayCommand(
            StartAsync,
            () => !IsMonitoring,
            HandleCommandError);
        StopCommand = new AsyncRelayCommand(
            StopAsync,
            () => IsMonitoring,
            HandleCommandError);

        if (refreshInterval is { } interval)
        {
            _refreshIntervalText = $"{Math.Max(1, interval.TotalSeconds):0} s";
        }

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
        _currentTimeText = DateTime.Now.ToString("HH:mm:ss");
    }

    public ObservableCollection<JobRowViewModel> Jobs { get; }
    public ICollectionView JobsView { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    public MonitoringState MonitoringState
    {
        get => _monitoringState;
        private set
        {
            if (SetProperty(ref _monitoringState, value))
            {
                OnPropertyChanged(nameof(MonitoringStateText));
                OnPropertyChanged(nameof(JobsEmptyText));
            }
        }
    }

    public string MonitoringStateText => _monitoringState.ToDisplay();

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(IsStopped));
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(JobsEmptyText));
            }
        }
    }

    public bool IsStopped => !_isMonitoring && _monitoringState == MonitoringState.Stopped;

    public int ActiveCount
    {
        get => _activeCount;
        private set => SetProperty(ref _activeCount, value);
    }

    public int CompletedCount
    {
        get => _completedCount;
        private set => SetProperty(ref _completedCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        private set => SetProperty(ref _failedCount, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int UnknownCount
    {
        get => _unknownCount;
        private set => SetProperty(ref _unknownCount, value);
    }

    public string UnknownDetailText
    {
        get => _unknownDetailText;
        private set => SetProperty(ref _unknownDetailText, value);
    }

    public string CostText
    {
        get => _costText;
        private set => SetProperty(ref _costText, value);
    }

    public string CostDetailText
    {
        get => _costDetailText;
        private set => SetProperty(ref _costDetailText, value);
    }

    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetProperty(ref _lastRefreshText, value);
    }

    public string LastSuccessfulObservationText
    {
        get => _lastSuccessfulObservationText;
        private set => SetProperty(ref _lastSuccessfulObservationText, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set => SetProperty(ref _currentTimeText, value);
    }

    public string ForgeDirectoryText
    {
        get => _forgeDirectoryText;
        private set => SetProperty(ref _forgeDirectoryText, value);
    }

    public string WarningsText
    {
        get => _warningsText;
        private set => SetProperty(ref _warningsText, value);
    }

    public bool HasWarnings
    {
        get => _hasWarnings;
        private set => SetProperty(ref _hasWarnings, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                JobsView.Refresh();
            }
        }
    }

    public JobRowViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetProperty(ref _selectedJob, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsSelectionEmpty));
                BeginRawLogLoad(value);
                BeginSelectedActivityLoad(value);
                BeginOriginalRequestLoad(value);
            }
        }
    }

    public bool HasSelection => _selectedJob is not null;
    public bool IsSelectionEmpty => _selectedJob is null;
    public bool IsJobsEmpty => Jobs.Count == 0;

    public string StateDetail
    {
        get => _stateDetail;
        private set => SetProperty(ref _stateDetail, value);
    }

    public string JobsSummaryText
    {
        get => _jobsSummaryText;
        private set => SetProperty(ref _jobsSummaryText, value);
    }

    public string OpenCodeStateText
    {
        get => _openCodeStateText;
        private set => SetProperty(ref _openCodeStateText, value);
    }

    public string RefreshIntervalText
    {
        get => _refreshIntervalText;
        private set => SetProperty(ref _refreshIntervalText, value);
    }

    public string JobsEmptyText => MonitoringState switch
    {
        MonitoringState.Stopped => "Start monitoring to discover forge-delegate jobs.",
        MonitoringState.Error => "Monitoring is in an error state. Stop and start again.",
        _ => "No forge jobs discovered yet. New jobs appear here automatically."
    };

    public async Task StartAsync()
    {
        if (IsMonitoring)
        {
            return;
        }

        ResetData();
        var cts = new CancellationTokenSource();
        _monitoringCts = cts;
        IsMonitoring = true;
        MonitoringState = MonitoringState.Idle;
        StateDetail = "Watching for new forge jobs";

        ForgeMonitoringService service;
        try
        {
            service = _serviceFactory();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cts.Dispose();
            _monitoringCts = null;
            IsMonitoring = false;
            MonitoringState = MonitoringState.Error;
            StateDetail = "Monitoring could not start.";
            HasWarnings = true;
            WarningsText = "Monitoring could not start.";
            return;
        }

        _monitoringTask = Task.Run(async () =>
        {
            try
            {
                await service.RunAsync(OnSnapshotAsync, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal Stop.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await DispatchAsync(() => HandleServiceFailure()).ConfigureAwait(false);
            }
        });
    }

    public async Task StopAsync()
    {
        var cts = _monitoringCts;
        var task = _monitoringTask;

        // Retained rows must stop presenting an active-target spinner as soon
        // as monitoring stops, even if no further snapshot is applied.
        SetRowsMonitoringActive(false);
        CancelSelectedActivityLoad();
        CancelOriginalRequestLoad();

        if (cts is null && !IsMonitoring)
        {
            MonitoringState = MonitoringState.Stopped;
            StateDetail = "Monitoring is stopped";
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Normal Stop.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HasWarnings = true;
                WarningsText = "Monitoring stopped after an unexpected error.";
            }
        }

        if (ReferenceEquals(_monitoringCts, cts))
        {
            _monitoringCts = null;
            _monitoringTask = null;
        }

        // A snapshot delivered while cancellation was completing must not
        // restore the active-target spinner after Stop.
        SetRowsMonitoringActive(false);
        cts?.Dispose();
        IsMonitoring = false;
        MonitoringState = MonitoringState.Stopped;
        StateDetail = "Monitoring is stopped";
        OnPropertyChanged(nameof(IsStopped));
    }

    /// <summary>
    /// Applies a snapshot. Exposed for deterministic UI-state tests; production
    /// callers reach this through the dispatcher from the monitoring loop.
    /// </summary>
    public void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ApplySnapshot(snapshot));
            return;
        }

        var previousSelectedCount = _selectedJob?.Snapshot.ParsedEventCount;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedIdentityChanged = false;

        foreach (var jobSnapshot in snapshot.Jobs)
        {
            seenIds.Add(jobSnapshot.JobId);
            if (!_jobsById.TryGetValue(jobSnapshot.JobId, out var row))
            {
                row = new JobRowViewModel(jobSnapshot);
                row.SelectedActivityChanged += OnJobSelectedActivityChanged;
                _jobsById[jobSnapshot.JobId] = row;
                Jobs.Add(row);
            }
            else
            {
                var previousSessionId = row.Snapshot.SessionId;
                var previousStartTicks = StartTicks(row.Snapshot.Metadata.Started);
                row.Update(jobSnapshot, snapshot.ObservedAt);
                var sessionChanged = !string.Equals(
                    previousSessionId,
                    jobSnapshot.SessionId,
                    StringComparison.Ordinal);
                var startChanged = previousStartTicks !=
                    StartTicks(jobSnapshot.Metadata.Started);
                if (sessionChanged || startChanged)
                {
                    _originalRequestService.InvalidateJob(jobSnapshot.JobId);
                    if (ReferenceEquals(_selectedJob, row))
                    {
                        CancelOriginalRequestLoad();
                        selectedIdentityChanged = true;
                    }
                }
            }

            row.SetMonitoringActive(
                snapshot.State is MonitoringState.Monitoring or MonitoringState.Idle);
        }

        if (selectedIdentityChanged)
        {
            BeginOriginalRequestLoad(_selectedJob);
        }

        foreach (var removedId in _jobsById.Keys.Where(id => !seenIds.Contains(id)).ToArray())
        {
            if (_jobsById.Remove(removedId, out var removed))
            {
                removed.SelectedActivityChanged -= OnJobSelectedActivityChanged;
                _originalRequestService.InvalidateJob(removedId);
                Jobs.Remove(removed);
                if (ReferenceEquals(_selectedJob, removed))
                {
                    SelectedJob = null;
                }
            }
        }

        JobsView.Refresh();
        if (_selectedJob is null && Jobs.Count > 0)
        {
            SelectedJob = JobsView.Cast<JobRowViewModel>().FirstOrDefault();
        }

        ActiveCount = snapshot.ActiveCount;
        CompletedCount = snapshot.CompletedCount;
        FailedCount = snapshot.FailedOrTimedOutCount;
        TotalCount = snapshot.TotalCount;
        UnknownCount = snapshot.UnknownCount;
        UnknownDetailText = $"{snapshot.UnknownCount} unknown · status not reported";
        JobsSummaryText = $"{snapshot.TotalCount} observed · {snapshot.UnknownCount} unknown";
        MonitoringState = snapshot.State;
        LastRefreshText = snapshot.ObservedAt.ToLocalTime().ToString("HH:mm:ss");
        if (snapshot.State != MonitoringState.Error)
        {
            LastSuccessfulObservationText = LastRefreshText;
        }

        ForgeDirectoryText = snapshot.ForgeDirectoryAvailable ? "Available" : "Not available";
        OnPropertyChanged(nameof(IsStopped));

        var warnings = snapshot.Warnings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HasWarnings = warnings.Length > 0;
        WarningsText = warnings.Length == 0
            ? "No monitoring errors."
            : string.Join(" · ", warnings);

        if (snapshot.ObservedCost is { } cost)
        {
            CostText = "$" + cost.ToString("0.0000", CultureInfo.InvariantCulture) +
                (snapshot.HasIncompleteCoverage ? " (incomplete)" : string.Empty);
            CostDetailText = snapshot.HasIncompleteCoverage
                ? "Observed subtotal; historical coverage is incomplete"
                : "Observed forge step cost";
        }
        else
        {
            CostText = "—";
            CostDetailText = "Not reported by forge metadata";
        }

        StateDetail = snapshot.State switch
        {
            MonitoringState.Monitoring => $"{snapshot.ActiveCount} confirmed active · {snapshot.UnknownCount} unknown",
            MonitoringState.Error => snapshot.Error ?? "Monitoring error",
            _ => $"{snapshot.UnknownCount} unknown · no confirmed active execution"
        };

        OnPropertyChanged(nameof(IsJobsEmpty));
        OnPropertyChanged(nameof(JobsEmptyText));

        if (_selectedJob is not null &&
            _selectedJob.Snapshot.ParsedEventCount != previousSelectedCount)
        {
            BeginRawLogLoad(_selectedJob);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        try
        {
            _monitoringCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        _rawLogCts?.Cancel();
        _rawLogCts?.Dispose();
        _rawLogCts = null;
        CancelSelectedActivityLoad();
        CancelOriginalRequestLoad();
        _originalRequestService.Dispose();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
    }

    private async Task OnSnapshotAsync(MonitoringSnapshot snapshot)
    {
        var cts = _monitoringCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await DispatchAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
        {
            // The window is closing or the dispatcher has shut down.
        }
    }

    private async Task DispatchAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }

    private void HandleServiceFailure()
    {
        if (_disposed)
        {
            return;
        }

        IsMonitoring = false;
        SetRowsMonitoringActive(false);
        CancelOriginalRequestLoad();
        MonitoringState = MonitoringState.Error;
        StateDetail = "Monitoring stopped after an unexpected error.";
        HasWarnings = true;
        WarningsText = "Monitoring stopped after an unexpected error.";
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        _monitoringTask = null;
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    private void HandleCommandError(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }

        HasWarnings = true;
        WarningsText = "A UI operation failed. Monitoring can be restarted.";
    }

    private void SetRowsMonitoringActive(bool active)
    {
        foreach (var row in Jobs)
        {
            row.SetMonitoringActive(active);
        }
    }

    private void ResetData()
    {
        _rawLogCts?.Cancel();
        _rawLogCts?.Dispose();
        _rawLogCts = null;
        CancelSelectedActivityLoad();
        CancelOriginalRequestLoad();
        _originalRequestService.ClearCache();

        SelectedJob = null;
        Jobs.Clear();
        _jobsById.Clear();
        ActiveCount = 0;
        CompletedCount = 0;
        FailedCount = 0;
        TotalCount = 0;
        UnknownCount = 0;
        UnknownDetailText = "0 unknown · status not reported";
        CostText = "—";
        CostDetailText = "Not reported by forge metadata";
        LastRefreshText = "—";
        LastSuccessfulObservationText = "—";
        ForgeDirectoryText = "Not checked";
        HasWarnings = false;
        WarningsText = "No monitoring errors.";
        JobsSummaryText = "0 observed · 0 unknown";
        OnPropertyChanged(nameof(IsJobsEmpty));
        OnPropertyChanged(nameof(IsSelectionEmpty));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        CurrentTimeText = DateTime.Now.ToString("HH:mm:ss");
        var now = DateTimeOffset.Now;
        foreach (var row in Jobs)
        {
            row.RefreshElapsed(now);
            row.RefreshDuration(now);
        }
    }

    private bool FilterJob(object item)
    {
        if (item is not JobRowViewModel job || string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        var search = _searchText.Trim();
        return Contains(job.JobId, search) ||
               Contains(job.ProjectText, search) ||
               Contains(job.ProjectTooltip, search) ||
               Contains(job.ModelText, search) ||
               Contains(job.StatusText, search) ||
               Contains(job.SessionText, search);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrEmpty(value) &&
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void OnJobSelectedActivityChanged(object? sender, EventArgs e)
    {
        if (sender is JobRowViewModel job && ReferenceEquals(job, _selectedJob))
        {
            BeginSelectedActivityLoad(job);
        }
    }

    private void BeginSelectedActivityLoad(JobRowViewModel? job)
    {
        if (job is null)
        {
            CancelSelectedActivityLoad();
            return;
        }

        var key = job.CurrentSelectedActivityLoadKey;
        var target = job.CreateSelectedActivityTarget();
        if (key is not { } loadKey || target is null)
        {
            CancelSelectedActivityLoad();
            return;
        }

        if (job.HasSelectedActivityLoadKey(loadKey))
        {
            return;
        }

        var reader = job.Snapshot.LiveActivity.FullTextReader;
        if (!job.IsSelectedActivityTextKind)
        {
            CancelSelectedActivityLoad();
            job.MarkSelectedActivityLoadStarted(loadKey);
            job.ApplySelectedActivityResult(
                loadKey,
                null,
                "N/A — complete text is available only for text activity.");
            return;
        }

        if (reader is null)
        {
            CancelSelectedActivityLoad();
            job.MarkSelectedActivityLoadStarted(loadKey);
            job.ApplySelectedActivityResult(
                loadKey,
                null,
                "N/A — complete text unavailable.");
            return;
        }

        CancelSelectedActivityLoad();
        var cts = new CancellationTokenSource();
        _selectedActivityCts = cts;
        job.MarkSelectedActivityLoadStarted(loadKey);
        _ = LoadSelectedActivitySafelyAsync(job, loadKey, target, reader, cts);
    }

    private async Task LoadSelectedActivitySafelyAsync(
        JobRowViewModel job,
        LiveActivityLoadKey key,
        FullReportTarget target,
        Func<FullReportTarget, FullReportResult> reader,
        CancellationTokenSource cts)
    {
        try
        {
            var result = await Task.Run(
                () => reader(target),
                cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested ||
                !ReferenceEquals(_selectedJob, job) ||
                !job.HasSelectedActivityLoadKey(key))
            {
                return;
            }

            job.ApplySelectedActivityResult(key, result, null);
        }
        catch (OperationCanceledException)
        {
            // Selection or monitoring changed while retrieval was running.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!cts.IsCancellationRequested &&
                ReferenceEquals(_selectedJob, job) &&
                job.HasSelectedActivityLoadKey(key))
            {
                job.ApplySelectedActivityResult(
                    key,
                    null,
                    "N/A — complete text unavailable.");
            }
        }
        finally
        {
            if (ReferenceEquals(_selectedActivityCts, cts))
            {
                _selectedActivityCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelSelectedActivityLoad()
    {
        var cts = _selectedActivityCts;
        _selectedActivityCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void BeginOriginalRequestLoad(JobRowViewModel? job)
    {
        CancelOriginalRequestLoad();
        if (job is null)
        {
            return;
        }

        var sessionId = job.Snapshot.SessionId;
        if (!OpenCodeIdentifiers.IsSafeSessionId(sessionId))
        {
            job.ClearOriginalRequest();
            return;
        }

        var startedAt = job.Snapshot.Metadata.Started;
        if (job.HasOriginalRequestFor(sessionId!, startedAt))
        {
            return;
        }

        // Never leave another association's or start time's text visible while
        // the new query is running. Successes are retained per job, session,
        // and exact start time; ambiguous and transient results are re-queried
        // on a later selection.
        job.ClearOriginalRequest();
        var cts = new CancellationTokenSource();
        _originalRequestCts = cts;
        _ = LoadOriginalRequestSafelyAsync(job, sessionId!, startedAt, cts);
    }

    private async Task LoadOriginalRequestSafelyAsync(
        JobRowViewModel job,
        string sessionId,
        DateTimeOffset? startedAt,
        CancellationTokenSource cts)
    {
        try
        {
            var result = await _originalRequestService.GetOriginalRequestAsync(
                job.JobId,
                sessionId,
                verifiedUserMessageId: null,
                cts.Token,
                jobStartedAt: startedAt).ConfigureAwait(true);

            if (cts.IsCancellationRequested ||
                !ReferenceEquals(_selectedJob, job) ||
                !string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
                StartTicks(job.Snapshot.Metadata.Started) != StartTicks(startedAt))
            {
                return;
            }

            job.ApplyOriginalRequestResult(sessionId, startedAt, result);
        }
        catch (OperationCanceledException)
        {
            // Selection, association, start time, or monitoring changed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never surface exception or CLI output text. The field stays empty.
            if (!cts.IsCancellationRequested &&
                ReferenceEquals(_selectedJob, job) &&
                string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) &&
                StartTicks(job.Snapshot.Metadata.Started) == StartTicks(startedAt))
            {
                job.ApplyOriginalRequestResult(
                    sessionId,
                    startedAt,
                    OpenCodeOriginalRequestResult.Unavailable(
                        OriginalRequestUnavailableReason.UnexpectedFailure));
            }
        }
        finally
        {
            if (ReferenceEquals(_originalRequestCts, cts))
            {
                _originalRequestCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelOriginalRequestLoad()
    {
        var cts = _originalRequestCts;
        _originalRequestCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private static long? StartTicks(DateTimeOffset? startedAt) =>
        startedAt?.ToUniversalTime().Ticks;

    private void BeginRawLogLoad(JobRowViewModel? job)
    {
        _ = LoadRawLogSafelyAsync(job);
    }

    private async Task LoadRawLogSafelyAsync(JobRowViewModel? job)
    {
        try
        {
            await LoadRawLogAsync(job).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Raw log display is best effort and never persisted.
        }
    }

    private async Task LoadRawLogAsync(JobRowViewModel? job)
    {
        _rawLogCts?.Cancel();
        _rawLogCts?.Dispose();
        _rawLogCts = null;

        if (job is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _rawLogCts = cts;
        var path = job.Snapshot.Metadata.LogPath;

        var preview = await Task.Run(
            () => ForgeLogTailReader.ReadTailPreview(path, _runsRoot),
            cts.Token).ConfigureAwait(true);

        if (cts.IsCancellationRequested || !ReferenceEquals(_selectedJob, job))
        {
            return;
        }

        job.SetRawLog(preview);
        if (ReferenceEquals(_rawLogCts, cts))
        {
            _rawLogCts = null;
            cts.Dispose();
        }
    }
}
