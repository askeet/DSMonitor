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
    private readonly Dictionary<string, JobRowViewModel> _jobsById =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private CancellationTokenSource? _rawLogCts;
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
    private string _refreshIntervalText = "5 s";

    public MainViewModel(
        Func<ForgeMonitoringService> serviceFactory,
        Dispatcher dispatcher,
        string runsRoot,
        TimeSpan? refreshInterval = null)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runsRoot = runsRoot;

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

        foreach (var jobSnapshot in snapshot.Jobs)
        {
            seenIds.Add(jobSnapshot.JobId);
            if (!_jobsById.TryGetValue(jobSnapshot.JobId, out var row))
            {
                row = new JobRowViewModel(jobSnapshot);
                _jobsById[jobSnapshot.JobId] = row;
                Jobs.Add(row);
            }
            else
            {
                row.Update(jobSnapshot, snapshot.ObservedAt);
            }
        }

        foreach (var removedId in _jobsById.Keys.Where(id => !seenIds.Contains(id)).ToArray())
        {
            if (_jobsById.Remove(removedId, out var removed))
            {
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
            CostText = "$" + cost.ToString("0.0000", CultureInfo.InvariantCulture);
            CostDetailText = "Observed forge step cost";
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

    private void ResetData()
    {
        _rawLogCts?.Cancel();
        _rawLogCts?.Dispose();
        _rawLogCts = null;

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
