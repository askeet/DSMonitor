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
    private readonly IOpenCodeActivityDurationService _activityDurationService;
    private readonly Dictionary<string, JobRowViewModel> _jobsById =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private CancellationTokenSource? _rawLogCts;
    private CancellationTokenSource? _selectedActivityCts;
    private CancellationTokenSource? _originalRequestCts;
    private CancellationTokenSource? _activityDurationCts;
    private string? _activityDurationRequestKey;
    private bool _disposed;
    private bool _monitoringHealthy = true;
    private bool _originalRequestMonitoringFailed;

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
    private bool _showActivityTextFilter = true;
    private bool _showActivityToolsFilter;
    private bool _showActivityStepsFilter;
    private IReadOnlyList<LiveActivityHistoryRowViewModel> _filteredActivityHistory =
        Array.Empty<LiveActivityHistoryRowViewModel>();
    private string _stateDetail = "Monitoring is stopped";
    private string _jobsSummaryText = "0 observed · 0 unknown";
    private string _openCodeStateText = "Not connected · Phase 2";
    private string _refreshIntervalText = "1 s";

    public MainViewModel(
        Func<ForgeMonitoringService> serviceFactory,
        Dispatcher dispatcher,
        string runsRoot,
        TimeSpan? refreshInterval = null,
        IOriginalRequestService? originalRequestService = null,
        IOpenCodeActivityDurationService? activityDurationService = null)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runsRoot = runsRoot;
        _originalRequestService =
            originalRequestService ?? DisabledOriginalRequestService.Instance;
        _activityDurationService =
            activityDurationService ?? DisabledOpenCodeActivityDurationService.Instance;

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

    /// <summary>Whether text entries are shown in Activity History.</summary>
    public bool ShowActivityTextFilter
    {
        get => _showActivityTextFilter;
        set => SetActivityFilter(
            value,
            ref _showActivityTextFilter,
            nameof(ShowActivityTextFilter));
    }

    /// <summary>Whether tool entries are shown in Activity History.</summary>
    public bool ShowActivityToolsFilter
    {
        get => _showActivityToolsFilter;
        set => SetActivityFilter(
            value,
            ref _showActivityToolsFilter,
            nameof(ShowActivityToolsFilter));
    }

    /// <summary>Whether step entries are shown in Activity History.</summary>
    public bool ShowActivityStepsFilter
    {
        get => _showActivityStepsFilter;
        set => SetActivityFilter(
            value,
            ref _showActivityStepsFilter,
            nameof(ShowActivityStepsFilter));
    }

    public JobRowViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (ReferenceEquals(_selectedJob, value))
            {
                return;
            }

            var previous = _selectedJob;
            if (SetProperty(ref _selectedJob, value))
            {
                if (previous is not null)
                {
                    previous.PropertyChanged -= OnSelectedJobPropertyChanged;
                    previous.ActivityHistoryUpdated -= OnSelectedJobActivityHistoryUpdated;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedJobPropertyChanged;
                    value.ActivityHistoryUpdated += OnSelectedJobActivityHistoryUpdated;
                }

                // Filters are job-scoped presentation state: every job starts
                // from the declared defaults.
                ResetActivityHistoryFilters();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsSelectionEmpty));
                RefreshActivityHistoryPresentation(ensureSelection: true);
                BeginRawLogLoad(value);
                BeginSelectedActivityLoad(value);
                BeginOriginalRequestLoad(value);
                BeginActivityDurationLoad(value);
            }
        }
    }

    private void ResetActivityHistoryFilters()
    {
        _showActivityTextFilter = true;
        _showActivityToolsFilter = false;
        _showActivityStepsFilter = false;
        OnPropertyChanged(nameof(ShowActivityTextFilter));
        OnPropertyChanged(nameof(ShowActivityToolsFilter));
        OnPropertyChanged(nameof(ShowActivityStepsFilter));
    }

    /// <summary>
    /// In-memory projection of the selected job's already bounded, merged
    /// history rows. No source read, ledger query, or selected-text retrieval is
    /// performed here; toggling a filter only re-evaluates this list.
    /// </summary>
    public IReadOnlyList<LiveActivityHistoryRowViewModel> FilteredActivityHistory =>
        _filteredActivityHistory;

    public string ActivityTextFilterLabel =>
        $"Text (✓{SelectedJob?.LiveActivityHistory.Count(row => row.IsTextActivity) ?? 0})";

    public string ActivityToolsFilterLabel
    {
        get
        {
            var rows = SelectedJob?.LiveActivityHistory ??
                Array.Empty<LiveActivityHistoryRowViewModel>();
            return $"Tools (✓{rows.Count(row => row.IsSuccessfulToolActivity)} · " +
                   $"✕{rows.Count(row => row.IsErrorToolActivity)})";
        }
    }

    public string ActivityStepsFilterLabel =>
        $"Steps ({SelectedJob?.LiveActivityHistory.Count(row => row.IsStepActivity) ?? 0})";

    private void SetActivityFilter(
        bool value,
        ref bool field,
        string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        RefreshActivityHistoryPresentation(ensureSelection: true);
    }

    private void OnSelectedJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobRowViewModel.LiveActivityHistory) or null)
        {
            // Labels, filtered rows, and the empty state; selection is settled
            // only after the job has restored its identity-based choice.
            RefreshActivityHistoryPresentation(ensureSelection: false);
        }
    }

    private void OnSelectedJobActivityHistoryUpdated(object? sender, EventArgs e) =>
        RefreshActivityHistoryPresentation(ensureSelection: true);

    /// <summary>
    /// Single presentation refresh: rebuilds the in-memory filtered projection,
    /// updates the job's filtered empty-state, publishes labels, and keeps the
    /// selection on a visible row. Counts always describe every eligible
    /// de-duplicated row, independent of the current filters.
    /// </summary>
    private void RefreshActivityHistoryPresentation(bool ensureSelection)
    {
        var job = SelectedJob;
        var rows = job?.LiveActivityHistory ??
            Array.Empty<LiveActivityHistoryRowViewModel>();
        var filtered = rows
            .Where(row =>
                (_showActivityTextFilter && row.IsTextActivity) ||
                (_showActivityToolsFilter && row.IsToolActivity) ||
                (_showActivityStepsFilter && row.IsStepActivity))
            .OrderBy(row => row.StartedSortTimestamp is null)
            .ThenByDescending(row => row.StartedSortTimestamp)
            .ThenByDescending(row => row.Entry.ByteOffset)
            .ToArray();

        if (!SequenceEqualByReference(_filteredActivityHistory, filtered))
        {
            _filteredActivityHistory = filtered;
            OnPropertyChanged(nameof(FilteredActivityHistory));
        }

        job?.SetActivityHistoryVisibility(filtered.Length);
        OnPropertyChanged(nameof(ActivityTextFilterLabel));
        OnPropertyChanged(nameof(ActivityToolsFilterLabel));
        OnPropertyChanged(nameof(ActivityStepsFilterLabel));

        // Re-published after the projection so the grid keeps a visible
        // selection; filter-driven re-selection does not start a source read.
        if (ensureSelection)
        {
            job?.EnsureSelectedActivityVisible(filtered);
        }
    }

    private static bool SequenceEqualByReference(
        IReadOnlyList<LiveActivityHistoryRowViewModel> left,
        IReadOnlyList<LiveActivityHistoryRowViewModel> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
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
        CancelActivityDurationLoad();

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
        var previousMonitoringHealthy = _monitoringHealthy;
        _monitoringHealthy = snapshot.State != MonitoringState.Error;
        var monitoringHealthChanged = previousMonitoringHealthy != _monitoringHealthy;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedIdentityChanged = false;
        var retryOriginalRequest = false;
        if (_originalRequestMonitoringFailed && _monitoringHealthy)
        {
            _originalRequestMonitoringFailed = false;
            selectedIdentityChanged = true;
        }
        var anyEvidenceChanged = false;
        var jobsChanged = false;
        var durationReloadRequired = false;

        foreach (var jobSnapshot in snapshot.Jobs)
        {
            seenIds.Add(jobSnapshot.JobId);
            if (!_jobsById.TryGetValue(jobSnapshot.JobId, out var row))
            {
                row = new JobRowViewModel(jobSnapshot);
                row.SelectedActivityChanged += OnJobSelectedActivityChanged;
                _jobsById[jobSnapshot.JobId] = row;
                Jobs.Add(row);
                jobsChanged = true;
            }
            else
            {
                var previousSessionId = row.Snapshot.SessionId;
                var previousStatus = row.Snapshot.Status;
                var previousStartTicks = StartTicks(row.Snapshot.Metadata.Started);
                var previousLifecycleOffset =
                    row.Snapshot.LiveActivity.LatestLifecycleByteOffset;
                var previousEvidenceFingerprint =
                    JobRowViewModel.CurrentEvidenceFingerprint(
                        row.Snapshot,
                        previousSessionId);
                row.Update(jobSnapshot, snapshot.ObservedAt);
                var lifecycleChanged =
                    previousLifecycleOffset != jobSnapshot.LiveActivity.LatestLifecycleByteOffset;
                var sessionChanged = !string.Equals(
                    previousSessionId,
                    jobSnapshot.SessionId,
                    StringComparison.Ordinal);
                var startChanged = previousStartTicks !=
                    StartTicks(jobSnapshot.Metadata.Started);
                var evidenceChanged = !string.Equals(
                    previousEvidenceFingerprint,
                    JobRowViewModel.CurrentEvidenceFingerprint(
                        row.Snapshot,
                        jobSnapshot.SessionId),
                    StringComparison.Ordinal);
                anyEvidenceChanged |= evidenceChanged;
                if (sessionChanged || startChanged || evidenceChanged ||
                    monitoringHealthChanged || !_monitoringHealthy)
                {
                    _originalRequestService.InvalidateJob(jobSnapshot.JobId);
                    _activityDurationService.InvalidateJob(jobSnapshot.JobId);
                    if (ReferenceEquals(_selectedJob, row))
                    {
                        CancelOriginalRequestLoad();
                        selectedIdentityChanged = true;
                        CancelActivityDurationLoad();
                        durationReloadRequired = true;
                    }
                    else if (evidenceChanged || monitoringHealthChanged || !_monitoringHealthy)
                    {
                        row.ClearOriginalRequest();
                        row.ClearMessageDurations();
                    }
                }

                if (lifecycleChanged && ReferenceEquals(_selectedJob, row))
                {
                    durationReloadRequired = true;
                }

                if ((lifecycleChanged || previousStatus != jobSnapshot.Status) &&
                    ReferenceEquals(_selectedJob, row) &&
                    OriginalRequestEvidence.Select(jobSnapshot, jobSnapshot.SessionId).State ==
                        OriginalRequestEvidenceState.None)
                {
                    retryOriginalRequest = true;
                }
            }

            row.SetMonitoringActive(
                snapshot.State is MonitoringState.Monitoring or MonitoringState.Idle);
        }

        if (selectedIdentityChanged || retryOriginalRequest)
        {
            if (retryOriginalRequest && _selectedJob is not null)
            {
                _originalRequestService.InvalidateJob(_selectedJob.JobId);
            }

            BeginOriginalRequestLoad(_selectedJob, forceReload: retryOriginalRequest);
        }

        foreach (var removedId in _jobsById.Keys.Where(id => !seenIds.Contains(id)).ToArray())
        {
            if (_jobsById.Remove(removedId, out var removed))
            {
                jobsChanged = true;
                removed.SelectedActivityChanged -= OnJobSelectedActivityChanged;
                _originalRequestService.InvalidateJob(removedId);
                _activityDurationService.InvalidateJob(removedId);
                removed.ClearMessageDurations();
                Jobs.Remove(removed);
                if (ReferenceEquals(_selectedJob, removed))
                {
                    SelectedJob = null;
                }
            }
        }

        if (jobsChanged || anyEvidenceChanged || monitoringHealthChanged)
        {
            foreach (var row in Jobs)
            {
                row.ClearMessageDurations();
            }

            if (_selectedJob is not null)
            {
                CancelActivityDurationLoad();
                durationReloadRequired = true;
            }
        }

        if (durationReloadRequired)
        {
            BeginActivityDurationLoad(_selectedJob);
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
        if (_selectedJob is not null)
        {
            _selectedJob.PropertyChanged -= OnSelectedJobPropertyChanged;
            _selectedJob.ActivityHistoryUpdated -= OnSelectedJobActivityHistoryUpdated;
        }

        CancelSelectedActivityLoad();
        CancelOriginalRequestLoad();
        CancelActivityDurationLoad();
        _originalRequestService.Dispose();
        _activityDurationService.Dispose();
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
        _originalRequestMonitoringFailed = true;
        _originalRequestService.ClearCache();
        CancelActivityDurationLoad();
        foreach (var row in Jobs)
        {
            row.ClearOriginalRequest();
            row.ClearMessageDurations();
        }

        _activityDurationService.ClearCache();
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
        CancelActivityDurationLoad();
        _originalRequestService.ClearCache();
        _activityDurationService.ClearCache();
        _monitoringHealthy = true;
        _originalRequestMonitoringFailed = false;

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

    private void BeginActivityDurationLoad(JobRowViewModel? job)
    {
        if (job is null)
        {
            CancelActivityDurationLoad();
            return;
        }

        var sessionId = job.Snapshot.SessionId;
        if (!_monitoringHealthy || !OpenCodeIdentifiers.IsSafeSessionId(sessionId))
        {
            CancelActivityDurationLoad();
            job.ClearMessageDurations();
            return;
        }

        var evidence = SelectDurationEvidence(job, sessionId!);
        if (evidence is not { } selection || selection.MessageIds.Count == 0)
        {
            CancelActivityDurationLoad();
            job.ClearMessageDurations();
            return;
        }

        if (job.HasCompleteMessageDurationsFor(
                sessionId!,
                selection.Fingerprint,
                selection.MessageIds))
        {
            CancelActivityDurationLoad();
            return;
        }

        var requestKey = job.JobId + "\n" +
                         sessionId + "\n" +
                         selection.Fingerprint + "\n" +
                         (job.Snapshot.LiveActivity.LatestLifecycleByteOffset?.ToString(
                             CultureInfo.InvariantCulture) ?? string.Empty);
        if (string.Equals(
                _activityDurationRequestKey,
                requestKey,
                StringComparison.Ordinal))
        {
            return;
        }

        CancelActivityDurationLoad();
        var cts = new CancellationTokenSource();
        _activityDurationCts = cts;
        _activityDurationRequestKey = requestKey;
        _ = LoadActivityDurationsSafelyAsync(
            job,
            sessionId!,
            selection.Fingerprint,
            selection.MessageIds,
            cts);
    }

    private async Task LoadActivityDurationsSafelyAsync(
        JobRowViewModel job,
        string sessionId,
        string evidenceFingerprint,
        IReadOnlyList<string> messageIds,
        CancellationTokenSource cts)
    {
        var startedAt = job.Snapshot.Metadata.Started;
        try
        {
            var result = await _activityDurationService.GetDurationsAsync(
                job.JobId,
                sessionId,
                messageIds,
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested ||
                !ReferenceEquals(_selectedJob, job) ||
                !_monitoringHealthy ||
                !string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
                StartTicks(job.Snapshot.Metadata.Started) != StartTicks(startedAt))
            {
                return;
            }

            var current = SelectDurationEvidence(job, sessionId);
            if (current is not { } selection ||
                !string.Equals(
                    selection.Fingerprint,
                    evidenceFingerprint,
                    StringComparison.Ordinal) ||
                !selection.MessageIds.SequenceEqual(messageIds, StringComparer.Ordinal))
            {
                return;
            }

            if (result.IsSuccess)
            {
                job.ApplyMessageDurations(
                    sessionId,
                    evidenceFingerprint,
                    messageIds,
                    result.Durations,
                    result.IsComplete);
            }
            else
            {
                job.ClearMessageDurations();
            }
        }
        catch (OperationCanceledException)
        {
            // Selection, evidence, health, or monitoring changed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!cts.IsCancellationRequested &&
                ReferenceEquals(_selectedJob, job) &&
                _monitoringHealthy &&
                string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) &&
                StartTicks(job.Snapshot.Metadata.Started) == StartTicks(startedAt))
            {
                job.ClearMessageDurations();
            }
        }
        finally
        {
            if (ReferenceEquals(_activityDurationCts, cts))
            {
                _activityDurationCts = null;
                _activityDurationRequestKey = null;
            }

            cts.Dispose();
        }
    }

    private void CancelActivityDurationLoad()
    {
        var cts = _activityDurationCts;
        _activityDurationCts = null;
        _activityDurationRequestKey = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// Builds the selected job's exact duration evidence, excluding any message
    /// id claimed by more than one currently observed job in the same session.
    /// </summary>
    private DurationEvidence? SelectDurationEvidence(
        JobRowViewModel job,
        string sessionId)
    {
        var selected = OriginalRequestEvidence.Select(job.Snapshot, sessionId);
        if (selected.State != OriginalRequestEvidenceState.Valid ||
            selected.MessageIds.Count == 0)
        {
            return null;
        }

        var claims = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in _jobsById.Values)
        {
            var otherSessionId = row.Snapshot.SessionId;
            if (!row.Snapshot.LiveActivity.IsHealthy ||
                !OpenCodeIdentifiers.IsSafeSessionId(otherSessionId))
            {
                continue;
            }

            foreach (var entry in row.Snapshot.LiveActivity.History)
            {
                if (!string.Equals(entry.JobId, row.JobId, StringComparison.Ordinal) ||
                    !string.Equals(entry.SessionId, otherSessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = otherSessionId + "\n" + entry.MessageId;
                if (!claims.TryGetValue(key, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    claims[key] = owners;
                }

                owners.Add(row.JobId);
            }
        }

        var allowed = selected.MessageIds
            .Where(messageId =>
                claims.TryGetValue(sessionId + "\n" + messageId, out var owners) &&
                owners.Count == 1)
            .ToArray();
        if (allowed.Length == 0)
        {
            return null;
        }

        return new DurationEvidence(
            allowed,
            OriginalRequestEvidence.ComputeFingerprint(job.JobId, sessionId, allowed));
    }

    private readonly record struct DurationEvidence(
        IReadOnlyList<string> MessageIds,
        string Fingerprint);

    private void BeginOriginalRequestLoad(JobRowViewModel? job, bool forceReload = false)
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
        var evidence = OriginalRequestEvidence.Select(job.Snapshot, sessionId);
        var evidenceFingerprint = evidence.Fingerprint;
        if (!_monitoringHealthy || _originalRequestMonitoringFailed)
        {
            job.ClearOriginalRequest();
            job.ApplyOriginalRequestResult(
                sessionId!,
                startedAt,
                evidenceFingerprint,
                OpenCodeOriginalRequestResult.None());
            return;
        }

        if (!forceReload && job.HasOriginalRequestFor(sessionId!, startedAt, evidenceFingerprint))
        {
            return;
        }

        // Never leave another association's, start time's, health state's, or
        // evidence set's text visible while the new query is running. Successes
        // are retained per job, session, start time, and exact observed evidence
        // set; ambiguous, unavailable, and transient results are re-queried on a
        // later selection.
        job.ClearOriginalRequest();
        if (evidence.State == OriginalRequestEvidenceState.Unavailable)
        {
            // Monitoring failure or an unavailable projection must not fall
            // back to stale session metadata or retry until healthy evidence.
            job.ApplyOriginalRequestResult(
                sessionId!,
                startedAt,
                evidenceFingerprint,
                OpenCodeOriginalRequestResult.None());
            return;
        }

        if (evidence.State == OriginalRequestEvidenceState.Conflicting)
        {
            // Contradictory Live Activity evidence is not an invitation to
            // fall back to the singleton heuristic.
            job.ApplyOriginalRequestResult(
                sessionId!,
                startedAt,
                evidenceFingerprint,
                OpenCodeOriginalRequestResult.Ambiguous());
            return;
        }

        var observedIds = evidence.MessageIds;
        var cts = new CancellationTokenSource();
        _originalRequestCts = cts;
        _ = LoadOriginalRequestSafelyAsync(
            job,
            sessionId!,
            startedAt,
            evidenceFingerprint,
            observedIds,
            cts);
    }

    private async Task LoadOriginalRequestSafelyAsync(
        JobRowViewModel job,
        string sessionId,
        DateTimeOffset? startedAt,
        string evidenceFingerprint,
        IReadOnlyList<string> observedIds,
        CancellationTokenSource cts)
    {
        try
        {
            var result = await _originalRequestService.GetOriginalRequestAsync(
                job.JobId,
                sessionId,
                verifiedUserMessageId: null,
                cts.Token,
                jobStartedAt: startedAt,
                observedAssistantMessageIds: observedIds).ConfigureAwait(true);

            if (cts.IsCancellationRequested ||
                !ReferenceEquals(_selectedJob, job) ||
                !string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
                StartTicks(job.Snapshot.Metadata.Started) != StartTicks(startedAt) ||
                !string.Equals(
                    JobRowViewModel.CurrentEvidenceFingerprint(job.Snapshot, sessionId),
                    evidenceFingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            job.ApplyOriginalRequestResult(
                sessionId,
                startedAt,
                evidenceFingerprint,
                result);
        }
        catch (OperationCanceledException)
        {
            // Selection, association, start time, evidence, or monitoring changed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never surface exception or CLI output text. The field stays empty.
            if (!cts.IsCancellationRequested &&
                ReferenceEquals(_selectedJob, job) &&
                string.Equals(job.Snapshot.SessionId, sessionId, StringComparison.Ordinal) &&
                StartTicks(job.Snapshot.Metadata.Started) == StartTicks(startedAt) &&
                string.Equals(
                    JobRowViewModel.CurrentEvidenceFingerprint(job.Snapshot, sessionId),
                    evidenceFingerprint,
                    StringComparison.Ordinal))
            {
                job.ApplyOriginalRequestResult(
                    sessionId,
                    startedAt,
                    evidenceFingerprint,
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
