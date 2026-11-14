using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using DSMonitor.Models;
using DSMonitor.Services;

namespace DSMonitor.ViewModels;

/// <summary>
/// Internal evidence basis for the table's Duration value. Reported metadata
/// and activity heuristics are deliberately distinct so the UI never presents
/// an inferred freeze as authoritative completion.
/// </summary>
internal enum JobDurationBasis
{
    Unavailable,
    Live,
    ObservedSpan,
    FinishedTerminalMetadata,
    FinishedReportedMetadata,
    FinishedActivityHeuristic
}

public sealed class JobRowViewModel : ObservableObject
{
    public const int MaxTimelineEvents = TimelineOrdering.DefaultCapacity;
    private static readonly TimeSpan UnknownActivityFreshnessWindow = TimeSpan.FromSeconds(30);

    private ForgeJobStatus _status = ForgeJobStatus.Unknown;
    private string _projectText = "N/A";
    private string _projectTooltip = string.Empty;
    private string _modelText = "—";
    private string _startedText = "—";
    private string _durationText = "N/A";
    private string _durationTooltip = "N/A — started or last activity is unavailable; duration is not inferred.";
    private double _durationSortSeconds = double.PositiveInfinity;
    private DateTimeOffset? _durationStartedAt;
    private JobDurationBasis _durationBasis = JobDurationBasis.Unavailable;
    private string _jobDisplayText = string.Empty;
    private string _elapsedText = "—";
    private string _lastActivityText = "—";
    private string _sessionText = "Not reported";
    private string _eventCountText = "0";
    private string _costText = "—";
    private string _deadlineText = "—";
    private string _deadlineDetail = "Not reported";
    private string _warningsText = string.Empty;
    private string _rawLogText = string.Empty;
    private bool _hasWarnings;
    private bool _hasRawLog;
    private bool _monitoringActive = true;
    private DateTimeOffset _currentTime;
    private IReadOnlyList<LiveActivityHistoryRowViewModel> _liveActivityHistory =
        Array.Empty<LiveActivityHistoryRowViewModel>();
    private LiveActivityHistoryRowViewModel? _selectedActivity;
    private string _selectedActivityText = "N/A";
    private bool _selectedActivityTextIsComplete;
    private string _selectedActivityCompletenessText = "No activity selected.";
    private LiveActivityLoadKey? _selectedActivityLoadKey;
    private string _originalRequestText = string.Empty;
    private string _originalRequestAttributionText = string.Empty;
    private string? _originalRequestSessionId;
    private long? _originalRequestStartTicks;
    private string? _originalRequestEvidenceFingerprint;
    private bool _originalRequestIsSuccess;
    private bool _originalRequestIsProvisional;
    private IReadOnlyDictionary<string, OpenCodeMessageDuration> _messageDurations =
        new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal);
    private string? _messageDurationSessionId;
    private string? _messageDurationEvidenceFingerprint;
    private IReadOnlySet<string> _messageDurationMessageIds =
        new HashSet<string>(StringComparer.Ordinal);
    private bool _messageDurationComplete;

    public JobRowViewModel(ForgeJobSnapshot snapshot)
    {
        Update(snapshot, snapshot.ObservedAt);
    }

    public ObservableCollection<MonitoringEvent> RecentEvents { get; } = new();
    public string JobId { get; private set; } = string.Empty;
    public string JobDisplayText => _jobDisplayText;
    public ForgeJobSnapshot Snapshot { get; private set; } = null!;
    public DateTimeOffset StartedSort { get; private set; } = DateTimeOffset.MinValue;
    public string StatusText => _status.ToDisplay();

    /// <summary>
    /// V1 reported-status display. The value comes from one unambiguous
    /// job-scoped agent text extraction and remains unverified; missing,
    /// ambiguous, unavailable, or ineligible evidence renders N/A.
    /// </summary>
    public string ReportedStatusText => ReportedStatusPresentation.ToDisplay(Snapshot.ReportedStatus);

    /// <summary>The complete untruncated agent-reported value shown in the cell.</summary>
    public string ReportedStatusTooltip => ReportedStatusPresentation.ToTooltip(Snapshot.ReportedStatus);

    /// <summary>
    /// Table cell value. It uses the same shared job-level resolution as the
    /// job's Live Activity history rows: the parsed reported value, then a
    /// job-owned text fallback, or the neutral placeholder. It remains
    /// observational only.
    /// </summary>
    public string ReportedStatusCellText =>
        ReportedStatusPresentation.ResolveJobReportedStatus(Snapshot).Value;

    /// <summary>
    /// Table cell tooltip. It uses the same normalized value as the
    /// corresponding activity history row.
    /// </summary>
    public string ReportedStatusCellTooltip => ReportedStatusCellText;

    /// <summary>
    /// True when Forge metadata reports an active job, or recent job-scoped
    /// activity supports a transient active indication for an UNKNOWN job,
    /// while monitoring is active and no exact job-scoped terminal step-finish
    /// is at or after the latest observed activity. A running job with no Doing
    /// record still qualifies; activity text alone never establishes completion.
    /// </summary>
    public bool IsActive => IsObservedActive(_currentTime);

    private bool IsObservedActive(DateTimeOffset now) =>
        _monitoringActive &&
        (_status.IsActive() || HasFreshUnknownJobActivity(now) || HasFreshLifecycleEvidence(now)) &&
        !HasTerminalActivityEvidenceAtOrAfterLatestActivity();

    private bool HasFreshUnknownJobActivity(DateTimeOffset now)
    {
        if (_status != ForgeJobStatus.Unknown ||
            Snapshot.LiveActivity.Doing is not { } doing ||
            !string.Equals(doing.JobId, JobId, StringComparison.Ordinal) ||
            doing.SourceTimestamp is not { } sourceTimestamp)
        {
            return false;
        }

        var age = now - sourceTimestamp;
        return age >= TimeSpan.Zero && age <= UnknownActivityFreshnessWindow;
    }

    private bool HasFreshLifecycleEvidence(DateTimeOffset now)
    {
        if (_status != ForgeJobStatus.Unknown ||
            Snapshot.LiveActivity.LatestLifecycleByteOffset is not { } latestOffset)
        {
            return false;
        }

        // An offset alone proves ordering, not recency. Require a source-time
        // stamped excerpt for the newest lifecycle offset before it can keep
        // UNKNOWN active; old offsets cannot sustain activity across clock ticks.
        var sourceTimestamp = Snapshot.LiveActivity.Doing is { } doing &&
                              string.Equals(doing.JobId, JobId, StringComparison.Ordinal) &&
                              doing.ByteOffset == latestOffset
            ? doing.SourceTimestamp
            : Snapshot.LiveActivity.History
                .Where(entry => string.Equals(entry.JobId, JobId, StringComparison.Ordinal) &&
                                entry.ByteOffset == latestOffset)
                .Select(entry => entry.SourceTimestamp)
                .FirstOrDefault(timestamp => timestamp is not null);

        if (sourceTimestamp is not { } timestamp)
        {
            return false;
        }

        var age = now - timestamp;
        return age >= TimeSpan.Zero && age <= UnknownActivityFreshnessWindow;
    }

    public string ProjectText => _projectText;
    public string ProjectTooltip => _projectTooltip;
    public string ModelText => _modelText;
    public string StartedText => _startedText;
    public string DurationText => _durationText;
    public string DurationTooltip => _durationTooltip;
    public double DurationSortSeconds => _durationSortSeconds;

    /// <summary>Internal evidence basis; tests use it to distinguish heuristic freezes.</summary>
    internal JobDurationBasis DurationBasis => _durationBasis;
    internal bool DurationUsesLiveClock => _durationBasis == JobDurationBasis.Live;
    internal bool DurationHasReportedCompletion =>
        _durationBasis is JobDurationBasis.FinishedTerminalMetadata or
            JobDurationBasis.FinishedReportedMetadata;
    internal bool DurationHasHeuristicCompletion =>
        _durationBasis == JobDurationBasis.FinishedActivityHeuristic;

    public string ElapsedText => _elapsedText;
    public string LastActivityText => _lastActivityText;
    public string SessionText => _sessionText;
    public string EventCountText => _eventCountText;
    public string CostText => _costText;
    public string DeadlineText => _deadlineText;
    public string DeadlineDetail => _deadlineDetail;
    public string WarningsText => _warningsText;
    public bool HasWarnings => _hasWarnings;
    public string RawLogText => _rawLogText;
    public bool HasRawLog => _hasRawLog;
    public string ChatText => Snapshot.LiveActivity.Doing?.SessionId ?? Snapshot.SessionId ?? "N/A";
    public string TaskText => "N/A — original request unavailable in the job capture";
    public string DoingText => Snapshot.LiveActivity.Doing?.Text ?? "N/A";
    public string DoneText => Snapshot.LiveActivity.Done?.Text ?? "N/A";
    public string DoingProvenance => FormatProvenance(Snapshot.LiveActivity.Doing);
    public string DoneProvenance => FormatProvenance(Snapshot.LiveActivity.Done);
    public string LiveActivityNotice => Snapshot.LiveActivity.Availability + " " + LiveActivitySnapshot.RetentionNotice;
    public string LiveUpdatedText => Snapshot.LiveActivity.Doing is { } activity
        ? activity.SourceTimestamp is { } sourceTime
            ? sourceTime.ToLocalTime().ToString("MMM dd HH:mm:ss")
            : "Observed " + activity.ObservedAt.ToLocalTime().ToString("MMM dd HH:mm:ss")
        : "N/A";

    /// <summary>
    /// Bounded, job-isolated history rows ordered newest source timestamp first.
    /// The value is transient and is replaced only from the selected job's own
    /// snapshot; selection changes never share rows between jobs.
    /// </summary>
    public IReadOnlyList<LiveActivityHistoryRowViewModel> LiveActivityHistory =>
        _liveActivityHistory;

    public bool HasLiveActivityHistory => _liveActivityHistory.Count > 0;

    /// <summary>
    /// Currently selected history row. Each job owns its own selection, so
    /// switching jobs never transfers or clears another job's chosen activity.
    /// The newest row is selected by default and a matching exact identity is
    /// retained across refreshes; if it disappears, selection falls back to the
    /// newest available row.
    /// </summary>
    public LiveActivityHistoryRowViewModel? SelectedActivity
    {
        get => _selectedActivity;
        set
        {
            if (ReferenceEquals(_selectedActivity, value))
            {
                return;
            }

            // A DataGrid resets SelectedItem to null when its ItemsSource or
            // DataContext changes. Never let that transient clear erase a
            // job's independent choice; a genuinely empty history still clears.
            if (value is null && _liveActivityHistory.Count > 0)
            {
                return;
            }

            if (value is not null &&
                !string.Equals(value.Entry.JobId, JobId, StringComparison.Ordinal))
            {
                return;
            }

            SetSelectedActivityValue(value);
        }
    }

    public string SelectedActivityText => _selectedActivityText;
    public bool SelectedActivityTextIsComplete => _selectedActivityTextIsComplete;
    public string SelectedActivityCompletenessText => _selectedActivityCompletenessText;

    internal event EventHandler? SelectedActivityChanged;

    /// <summary>
    /// Applies the global monitoring lifecycle. Stopped or failed monitoring
    /// must not leave an active-target spinner running on retained rows, even
    /// before a new snapshot arrives.
    /// </summary>
    internal void SetMonitoringActive(bool active)
    {
        if (_monitoringActive == active)
        {
            return;
        }

        var wasActive = IsActive;
        _monitoringActive = active;
        UpdateLiveActivityHistory(Snapshot);
        if (wasActive != IsActive)
        {
            OnPropertyChanged(nameof(IsActive));
        }

        RefreshDuration(_currentTime);
    }

    internal bool IsSelectedActivityTextKind =>
        _selectedActivity is { } row &&
        row.Entry.Kind.Contains("text", StringComparison.OrdinalIgnoreCase);

    internal LiveActivityLoadKey? CurrentSelectedActivityLoadKey =>
        _selectedActivity is { } row ? LiveActivityLoadKey.FromRow(row) : null;

    internal FullReportTarget? CreateSelectedActivityTarget()
    {
        if (_selectedActivity is not { } row)
        {
            return null;
        }

        var entry = row.Entry;
        return new FullReportTarget(
            entry.JobId,
            entry.SessionId,
            entry.MessageId,
            entry.PartId,
            entry.ByteOffset,
            entry.RecordDigest);
    }

    internal bool HasSelectedActivityLoadKey(LiveActivityLoadKey key) =>
        _selectedActivityLoadKey == key;

    internal void MarkSelectedActivityLoadStarted(LiveActivityLoadKey key)
    {
        if (CurrentSelectedActivityLoadKey != key)
        {
            return;
        }

        _selectedActivityLoadKey = key;
        _selectedActivityCompletenessText = "Retrieving source text…";
        OnPropertyChanged(nameof(SelectedActivityCompletenessText));
    }

    internal void ApplySelectedActivityResult(
        LiveActivityLoadKey key,
        FullReportResult? result,
        string? unavailableReason)
    {
        if (_selectedActivity is not { } row ||
            CurrentSelectedActivityLoadKey != key)
        {
            return;
        }

        if (result is { IsComplete: true, Text: { } completeText })
        {
            var text = LiveActivityTextPolicy.SanitizeForDisplay(
                completeText,
                out var sanitized);
            _selectedActivityText = text;
            _selectedActivityTextIsComplete = !sanitized;
            _selectedActivityCompletenessText = sanitized
                ? "Incomplete — unsafe control characters were omitted."
                : string.Empty;
        }
        else
        {
            _selectedActivityText = row.Entry.Text;
            _selectedActivityTextIsComplete = false;
            _selectedActivityCompletenessText = ShortCompleteness(result, unavailableReason);
        }

        RaiseSelectedActivityDisplayChanged();
    }

    private static string ShortCompleteness(
        FullReportResult? result,
        string? unavailableReason)
    {
        if (result is null)
        {
            return string.IsNullOrWhiteSpace(unavailableReason)
                ? "N/A — source text unavailable."
                : unavailableReason;
        }

        return result.Status switch
        {
            FullReportStatus.Incomplete =>
                "Incomplete — source text is outside the safely retrieved window.",
            FullReportStatus.Unavailable => "N/A — source text unavailable.",
            FullReportStatus.Unsafe => "N/A — source text could not be safely verified.",
            _ => "N/A — complete source text unavailable."
        };
    }

    private void SetSelectedActivityValue(LiveActivityHistoryRowViewModel? value)
    {
        var previousKey = _selectedActivityLoadKey;
        var nextKey = value is null ? (LiveActivityLoadKey?)null : LiveActivityLoadKey.FromRow(value);
        _selectedActivity = value;

        if (value is null)
        {
            _selectedActivityLoadKey = null;
            _selectedActivityText = "N/A";
            _selectedActivityTextIsComplete = false;
            _selectedActivityCompletenessText = "No activity selected.";
        }
        else if (previousKey == nextKey)
        {
            // Same exact source revision: keep the already retrieved full text
            // while refreshing the row object used for spinner/status display.
        }
        else
        {
            _selectedActivityLoadKey = null;
            _selectedActivityText = value.Entry.Text;
            _selectedActivityTextIsComplete = false;
            _selectedActivityCompletenessText = "Incomplete — source text not yet verified.";
        }

        OnPropertyChanged(nameof(SelectedActivity));
        RaiseSelectedActivityDisplayChanged();
        SelectedActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSelectedActivityDisplayChanged()
    {
        OnPropertyChanged(nameof(SelectedActivityText));
        OnPropertyChanged(nameof(SelectedActivityTextIsComplete));
        OnPropertyChanged(nameof(SelectedActivityCompletenessText));
    }

    /// <summary>
    /// Exact, untruncated original-request text for the selected job. It is
    /// empty when the request is unavailable, ambiguous, or not yet loaded.
    /// A successful singleton or timestamp result remains explicitly
    /// best-effort; only an explicitly verified correlation is job-specific.
    /// </summary>
    public string OriginalRequestText => _originalRequestText;

    /// <summary>
    /// Short attribution label for the request. It is never a substitute for
    /// the exact text and is empty for unavailable or absent results.
    /// </summary>
    public string OriginalRequestAttributionText => _originalRequestAttributionText;

    internal bool HasOriginalRequestFor(
        string sessionId,
        DateTimeOffset? startedAt,
        string evidenceFingerprint) =>
        _originalRequestIsSuccess &&
        !_originalRequestIsProvisional &&
        string.Equals(_originalRequestSessionId, sessionId, StringComparison.Ordinal) &&
        _originalRequestStartTicks == StartTicks(startedAt) &&
        string.Equals(
            _originalRequestEvidenceFingerprint,
            evidenceFingerprint,
            StringComparison.Ordinal);

    internal void ApplyOriginalRequestResult(
        string sessionId,
        DateTimeOffset? startedAt,
        string evidenceFingerprint,
        OpenCodeOriginalRequestResult result)
    {
        if (!string.Equals(Snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
            StartTicks(Snapshot.Metadata.Started) != StartTicks(startedAt) ||
            !string.Equals(
                CurrentEvidenceFingerprint(sessionId),
                evidenceFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        var text = result.IsSuccess ? result.Text : string.Empty;
        var label = result.Status is OriginalRequestStatus.BestEffortSingleton or
            OriginalRequestStatus.VerifiedJobCorrelation or
            OriginalRequestStatus.ObservedJobCorrelation or
            OriginalRequestStatus.BestEffortTimeCorrelation or
            OriginalRequestStatus.Ambiguous
            ? result.Label
            : string.Empty;

        _originalRequestSessionId = result.IsSuccess ? sessionId : null;
        _originalRequestStartTicks = result.IsSuccess ? StartTicks(startedAt) : null;
        _originalRequestEvidenceFingerprint = result.IsSuccess ? evidenceFingerprint : null;
        _originalRequestIsSuccess = result.IsSuccess;
        _originalRequestIsProvisional = result.Status == OriginalRequestStatus.BestEffortTimeCorrelation;
        SetText(text, ref _originalRequestText, nameof(OriginalRequestText));
        SetText(
            label,
            ref _originalRequestAttributionText,
            nameof(OriginalRequestAttributionText));
    }

    internal static string CurrentEvidenceFingerprint(
        ForgeJobSnapshot snapshot,
        string? sessionId) =>
        OriginalRequestEvidence.Select(snapshot, sessionId).Fingerprint;

    private string CurrentEvidenceFingerprint(string sessionId) =>
        CurrentEvidenceFingerprint(Snapshot, sessionId);

    internal void ClearOriginalRequest()
    {
        _originalRequestSessionId = null;
        _originalRequestStartTicks = null;
        _originalRequestEvidenceFingerprint = null;
        _originalRequestIsSuccess = false;
        _originalRequestIsProvisional = false;
        SetText(string.Empty, ref _originalRequestText, nameof(OriginalRequestText));
        SetText(
            string.Empty,
            ref _originalRequestAttributionText,
            nameof(OriginalRequestAttributionText));
    }

    internal bool HasCompleteMessageDurationsFor(
        string sessionId,
        string evidenceFingerprint,
        IReadOnlyList<string> messageIds) =>
        _messageDurationComplete &&
        string.Equals(_messageDurationSessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(
            _messageDurationEvidenceFingerprint,
            evidenceFingerprint,
            StringComparison.Ordinal) &&
        _messageDurationMessageIds.SetEquals(messageIds);

    /// <summary>
    /// Applies only exact message ids from the selected job's verified evidence
    /// set. The values are transient and never feed the job table duration,
    /// spinner termination, cost, or persistence.
    /// </summary>
    internal void ApplyMessageDurations(
        string sessionId,
        string evidenceFingerprint,
        IReadOnlyList<string> messageIds,
        IReadOnlyDictionary<string, OpenCodeMessageDuration> durations,
        bool isComplete)
    {
        if (!string.Equals(
                Snapshot.SessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        var accepted = new Dictionary<string, OpenCodeMessageDuration>(
            StringComparer.Ordinal);
        foreach (var id in messageIds)
        {
            if (durations.TryGetValue(id, out var duration) &&
                string.Equals(duration.MessageId, id, StringComparison.Ordinal))
            {
                accepted[id] = duration;
            }
        }

        _messageDurations = accepted;
        _messageDurationSessionId = sessionId;
        _messageDurationEvidenceFingerprint = evidenceFingerprint;
        _messageDurationMessageIds = new HashSet<string>(
            messageIds,
            StringComparer.Ordinal);
        _messageDurationComplete =
            isComplete && accepted.Count == _messageDurationMessageIds.Count;
        UpdateLiveActivityHistory(Snapshot);
    }

    internal void ClearMessageDurations()
    {
        if (!HasAnyMessageDurations)
        {
            return;
        }

        ResetMessageDurations();
        if (Snapshot is not null)
        {
            UpdateLiveActivityHistory(Snapshot);
        }
    }

    private bool HasAnyMessageDurations =>
        _messageDurations.Count > 0 ||
        _messageDurationSessionId is not null ||
        _messageDurationEvidenceFingerprint is not null;

    private void ResetMessageDurations()
    {
        _messageDurations = new Dictionary<string, OpenCodeMessageDuration>(
            StringComparer.Ordinal);
        _messageDurationSessionId = null;
        _messageDurationEvidenceFingerprint = null;
        _messageDurationMessageIds = new HashSet<string>(StringComparer.Ordinal);
        _messageDurationComplete = false;
    }

    public string LiveActivityHistoryNotice => LiveActivitySnapshot.HistoryRetentionNotice;

    public string LiveActivityHistoryEmptyText => "No activity observed.";
    public string StatusProvenance => $"{ForgeStatusLabel}; Forge metadata {Snapshot.Metadata.MetadataPath}; observed {Snapshot.ObservedAt:O}";

    private static string FormatProvenance(ActivityExcerpt? activity) => activity is null
        ? "N/A — no safely attributed record in the bounded source window"
        : $"{activity.Kind}; job {activity.JobId}; session {activity.SessionId}; message {activity.MessageId}; " +
          $"part {activity.PartId ?? "N/A"}; source {activity.SourcePath} at byte {activity.ByteOffset}; " +
          $"source time {activity.SourceTimestamp?.ToString("O") ?? "N/A"}; observed {activity.ObservedAt:O}. " +
          "Captured observation; may be historical. No independent QA or user acceptance established.";
    public string ForgeStatusLabel => _status switch
    {
        ForgeJobStatus.Running => "Execution confirmed by metadata",
        ForgeJobStatus.Done => "Completion reported by metadata",
        ForgeJobStatus.Failed => "Failure reported by metadata",
        ForgeJobStatus.Interrupted => "Interruption reported by metadata",
        ForgeJobStatus.TimedOut => "Forge wrapper timed out; worker state unverified",
        ForgeJobStatus.Blocked => "Blocker reported by metadata",
        _ => "Status not reported by forge metadata"
    };

    public void Update(ForgeJobSnapshot snapshot, DateTimeOffset now)
    {
        _currentTime = now;
        var previousSessionId = Snapshot is { } previous ? previous.SessionId : null;
        var previousStartTicks = Snapshot is { } prior
            ? StartTicks(prior.Metadata.Started)
            : null;
        if (!string.Equals(previousSessionId, snapshot.SessionId, StringComparison.Ordinal) ||
            previousStartTicks != StartTicks(snapshot.Metadata.Started))
        {
            ClearOriginalRequest();
            ResetMessageDurations();
        }

        Snapshot = snapshot;
        JobId = snapshot.JobId;
        StartedSort = snapshot.Metadata.Started ?? DateTimeOffset.MinValue;
        SetText(FormatJobDisplay(snapshot.JobId), ref _jobDisplayText, nameof(JobDisplayText));

        if (_status != snapshot.Status)
        {
            _status = snapshot.Status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ForgeStatusLabel));
        }

        var directory = snapshot.Metadata.Directory;
        SetText(
            TryGetProjectLeaf(directory, out var projectLeaf) ? projectLeaf : "N/A",
            ref _projectText,
            nameof(ProjectText));
        SetText(directory ?? string.Empty, ref _projectTooltip, nameof(ProjectTooltip));
        SetText(snapshot.Metadata.Model ?? "—", ref _modelText, nameof(ModelText));
        SetText(
            snapshot.Metadata.Started is { } started
                ? started.ToLocalTime().ToString("MMM dd, HH:mm")
                : "—",
            ref _startedText,
            nameof(StartedText));
        UpdateDuration(snapshot, now);
        SetText(snapshot.SessionId ?? "Not reported", ref _sessionText, nameof(SessionText));
        SetText(snapshot.ParsedEventCount.ToString(), ref _eventCountText, nameof(EventCountText));
        SetText(snapshot.CostText, ref _costText, nameof(CostText));
        SetText(snapshot.DeadlineText, ref _deadlineText, nameof(DeadlineText));
        SetText(snapshot.DeadlineDetail, ref _deadlineDetail, nameof(DeadlineDetail));
        SetText(snapshot.Warnings.Count == 0 ? string.Empty : string.Join(Environment.NewLine, snapshot.Warnings), ref _warningsText, nameof(WarningsText));
        SetText(
            snapshot.LastActivity is { } activity
                ? activity.ToLocalTime().ToString("HH:mm:ss")
                : "—",
            ref _lastActivityText,
            nameof(LastActivityText));

        var hasWarnings = snapshot.Warnings.Count > 0;
        if (_hasWarnings != hasWarnings)
        {
            _hasWarnings = hasWarnings;
            OnPropertyChanged(nameof(HasWarnings));
        }

        MergeEvents(snapshot.Events);
        UpdateLiveActivityHistory(snapshot);
        RefreshElapsed(now);
        OnPropertyChanged(nameof(Snapshot));
        foreach (var property in new[] { nameof(ChatText), nameof(TaskText), nameof(DoingText),
                     nameof(DoneText), nameof(DoingProvenance), nameof(DoneProvenance),
                     nameof(LiveActivityNotice), nameof(LiveUpdatedText), nameof(StatusProvenance),
                     nameof(IsActive),
                     nameof(ReportedStatusText), nameof(ReportedStatusTooltip),
                     nameof(ReportedStatusCellText), nameof(ReportedStatusCellTooltip) })
            OnPropertyChanged(property);
    }

    private bool HasTerminalActivityEvidenceAtOrAfterLatestActivity()
    {
        var terminalOffset = Snapshot.LiveActivity.DoneEventByteOffset;
        if (terminalOffset is null)
        {
            if (Snapshot.LiveActivity.Done is not { } done ||
                !string.Equals(done.JobId, JobId, StringComparison.Ordinal))
            {
                return false;
            }

            terminalOffset = done.ByteOffset;
        }

        var latestActivityOffset = long.MinValue;
        if (Snapshot.LiveActivity.LatestLifecycleByteOffset is { } lifecycleOffset)
        {
            latestActivityOffset = lifecycleOffset;
        }

        if (Snapshot.LiveActivity.Doing is { } doing &&
            string.Equals(doing.JobId, JobId, StringComparison.Ordinal))
        {
            latestActivityOffset = Math.Max(latestActivityOffset, doing.ByteOffset);
        }

        foreach (var entry in Snapshot.LiveActivity.History)
        {
            if (string.Equals(entry.JobId, JobId, StringComparison.Ordinal))
            {
                latestActivityOffset = Math.Max(latestActivityOffset, entry.ByteOffset);
            }
        }

        return terminalOffset.Value >= latestActivityOffset;
    }

    public void SetRawLog(string? text)
    {
        var hasRawLog = text is not null;
        if (SetProperty(ref _rawLogText, text ?? string.Empty, nameof(RawLogText)))
        {
            _hasRawLog = hasRawLog;
            OnPropertyChanged(nameof(HasRawLog));
        }
    }

    /// <summary>
    /// Advances the table's live Duration without reading logs. The shared
    /// dispatcher clock calls this about once per second; finished and
    /// observed-span rows are left frozen.
    /// </summary>
    public void RefreshDuration(DateTimeOffset now)
    {
        var wasActive = IsActive;
        _currentTime = now;
        var isActive = IsActive;
        if (wasActive != isActive)
        {
            OnPropertyChanged(nameof(IsActive));
        }

        if (_durationStartedAt is not { } started)
        {
            return;
        }

        if (isActive)
        {
            _durationBasis = JobDurationBasis.Live;
            SetLiveDuration(started, now);
            return;
        }

        if (_durationBasis == JobDurationBasis.Live)
        {
            if (_status == ForgeJobStatus.Unknown)
            {
                UpdateDuration(Snapshot, now);
            }
            else
            {
                // Preserve the last supported duration at the moment the
                // shared activity decision stops advancing it.
                SetLiveDuration(started, now);
                _durationBasis = JobDurationBasis.ObservedSpan;
            }
        }
    }

    public void RefreshElapsed(DateTimeOffset now)
    {
        // Original right-panel behavior: active/unknown jobs use the wall clock;
        // completed jobs use their last observed activity. The table's Duration
        // column uses RefreshDuration and keeps separate evidence.
        var started = Snapshot.Metadata.Started;
        if (started is null)
        {
            SetText("—", ref _elapsedText, nameof(ElapsedText));
            return;
        }

        if (_status.IsActive() || _status == ForgeJobStatus.Unknown)
        {
            SetText(FormatDuration(now - started.Value), ref _elapsedText, nameof(ElapsedText));
            return;
        }

        if (Snapshot.LastActivity is { } lastActivity && lastActivity > started.Value)
        {
            SetText(FormatDuration(lastActivity - started.Value), ref _elapsedText, nameof(ElapsedText));
        }
        else
        {
            SetText("—", ref _elapsedText, nameof(ElapsedText));
        }
    }

    private void UpdateDuration(ForgeJobSnapshot snapshot, DateTimeOffset now)
    {
        var hadLiveDuration = _durationBasis == JobDurationBasis.Live;
        _durationStartedAt = snapshot.Metadata.Started;
        if (snapshot.Metadata.Started is not { } started)
        {
            _durationBasis = JobDurationBasis.Unavailable;
            SetDurationUnavailable(
                "N/A — started is unavailable; duration is not inferred.");
            return;
        }

        // Last accepted activity is the best observed finish; the bounded Done
        // excerpt is a fallback when no event timestamp is available and the
        // excerpt still belongs to this job.
        var doneSourceTimestamp =
            snapshot.LiveActivity.Done is { } done &&
            string.Equals(done.JobId, snapshot.JobId, StringComparison.Ordinal)
                ? done.SourceTimestamp
                : null;
        var observedFinish = snapshot.LastActivity ?? doneSourceTimestamp;

        switch (snapshot.Status)
        {
            case ForgeJobStatus.Failed:
            case ForgeJobStatus.Interrupted:
            case ForgeJobStatus.TimedOut:
                SetFinishedDuration(
                    JobDurationBasis.FinishedTerminalMetadata,
                    started,
                    observedFinish,
                    $"Forge metadata reports {snapshot.Status.ToDisplay()}.");
                return;

            case ForgeJobStatus.Done:
                SetFinishedDuration(
                    JobDurationBasis.FinishedReportedMetadata,
                    started,
                    observedFinish,
                    "Forge metadata reports completion; not independently verified.");
                return;

            case ForgeJobStatus.Running:
            case ForgeJobStatus.Blocked:
                if (!IsActive)
                {
                    SetObservedSpan(snapshot, started, hadLiveDuration, now);
                    return;
                }

                _durationBasis = JobDurationBasis.Live;
                SetLiveDuration(started, now);
                return;

            default:
                if (IsActive)
                {
                    _durationBasis = JobDurationBasis.Live;
                    SetLiveDuration(started, now);
                    return;
                }

                if (TryGetDoneActivityFinish(snapshot, out var doneAt))
                {
                    SetFinishedDuration(
                        JobDurationBasis.FinishedActivityHeuristic,
                        started,
                        doneAt,
                        "Frozen when the latest observed activity was a completed agent result; " +
                        "heuristic only, not authoritative completion.");
                }
                else
                {
                    SetObservedSpan(snapshot, started, hadLiveDuration, now);
                }

                return;
        }
    }

    private void SetLiveDuration(DateTimeOffset started, DateTimeOffset now)
    {
        var duration = now - started;
        var clockSkew = duration < TimeSpan.Zero;
        if (clockSkew)
        {
            duration = TimeSpan.Zero;
        }

        SetProperty(ref _durationSortSeconds, duration.TotalSeconds, nameof(DurationSortSeconds));
        SetText(FormatDuration(duration), ref _durationText, nameof(DurationText));
        SetText(
            $"Live duration {FormatDurationDetail(duration)} " +
            $"(started {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}). " +
            "Updated about once per second from the Forge metadata start; " +
            "not proof of execution or worker liveness." +
            (clockSkew ? " Observation time precedes started; shown as 0s." : string.Empty),
            ref _durationTooltip,
            nameof(DurationTooltip));
    }

    private void SetFinishedDuration(
        JobDurationBasis basis,
        DateTimeOffset started,
        DateTimeOffset? finishedAt,
        string evidence)
    {
        _durationBasis = basis;
        if (finishedAt is not { } finish)
        {
            SetDurationUnavailable(
                $"N/A — {DescribeBasis(basis)} but no finish timestamp is available; " +
                "duration is not inferred.");
            return;
        }

        if (finish < started)
        {
            SetDurationUnavailable(
                $"N/A — {DescribeBasis(basis)} and the finish timestamp precedes started; " +
                "duration is invalid and is not inferred.");
            return;
        }

        var duration = finish - started;
        SetProperty(ref _durationSortSeconds, duration.TotalSeconds, nameof(DurationSortSeconds));
        SetText(FormatDuration(duration), ref _durationText, nameof(DurationText));
        SetText(
            $"Observed duration {FormatDurationDetail(duration)} " +
            $"(started {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}, " +
            $"finish {finish.ToLocalTime():yyyy-MM-dd HH:mm:ss}). {evidence} " +
            "Frozen; no live timer.",
            ref _durationTooltip,
            nameof(DurationTooltip));
    }

    private void SetObservedSpan(
        ForgeJobSnapshot snapshot,
        DateTimeOffset started,
        bool preserveLiveDuration = false,
        DateTimeOffset? now = null)
    {
        _durationBasis = JobDurationBasis.ObservedSpan;
        if (snapshot.LastActivity is not { } lastActivity)
        {
            if (TryRetainPreviousDuration(
                    started,
                    now ?? snapshot.ObservedAt,
                    preserveLiveDuration,
                    "No activity timestamp is available."))
            {
                return;
            }

            SetDurationUnavailable(
                "N/A — started or last activity is unavailable; duration is not inferred.");
            return;
        }

        if (lastActivity < started)
        {
            if (TryRetainPreviousDuration(
                    started,
                    now ?? snapshot.ObservedAt,
                    preserveLiveDuration,
                    "The latest activity timestamp precedes started."))
            {
                return;
            }

            SetDurationUnavailable(
                "N/A — last activity precedes started; duration is invalid and is not inferred.");
            return;
        }

        var duration = lastActivity - started;
        SetProperty(ref _durationSortSeconds, duration.TotalSeconds, nameof(DurationSortSeconds));
        SetText(FormatDuration(duration), ref _durationText, nameof(DurationText));
        SetText(
            $"Observed duration {FormatDurationDetail(duration)} " +
            $"(started {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}, " +
            $"last activity {lastActivity.ToLocalTime():yyyy-MM-dd HH:mm:ss}). " +
            "Status is UNKNOWN or unconfirmed, so the live clock is not extended; " +
            "observed span only, not proof of completion. No live timer.",
            ref _durationTooltip,
            nameof(DurationTooltip));
    }

    private bool TryRetainPreviousDuration(
        DateTimeOffset started,
        DateTimeOffset now,
        bool wasLive,
        string reason)
    {
        var hasPreviousValue = double.IsFinite(_durationSortSeconds) && _durationText != "N/A";
        if (!wasLive && !hasPreviousValue)
        {
            return false;
        }

        if (wasLive)
        {
            SetLiveDuration(started, now);
        }

        _durationBasis = JobDurationBasis.ObservedSpan;
        SetText(
            $"Previously supported duration retained because {reason} " +
            "It is frozen and is not proof of execution or completion.",
            ref _durationTooltip,
            nameof(DurationTooltip));
        return true;
    }

    /// <summary>
    /// Weak completion evidence from an already job-scoped done excerpt. It is
    /// accepted only when the done record belongs to this job and no later
    /// text/tool record was observed. A done record followed by newer activity
    /// (an intermediate step finish) never freezes the row, and a repeated
    /// done record is never treated as more than the latest observed record.
    /// </summary>
    private static bool TryGetDoneActivityFinish(
        ForgeJobSnapshot snapshot,
        out DateTimeOffset finish)
    {
        finish = default;
        var done = snapshot.LiveActivity.Done;
        if (done is null ||
            string.IsNullOrWhiteSpace(done.Text) ||
            !string.Equals(done.JobId, snapshot.JobId, StringComparison.Ordinal))
        {
            return false;
        }

        if (snapshot.LiveActivity.Doing is { } doing)
        {
            if (!string.Equals(doing.JobId, snapshot.JobId, StringComparison.Ordinal) ||
                doing.ByteOffset > done.ByteOffset)
            {
                return false;
            }
        }

        finish = done.SourceTimestamp ?? snapshot.LastActivity ?? done.ObservedAt;
        return true;
    }

    private static string DescribeBasis(JobDurationBasis basis) => basis switch
    {
        JobDurationBasis.FinishedTerminalMetadata => "a terminal status is reported",
        JobDurationBasis.FinishedReportedMetadata => "completion is reported",
        JobDurationBasis.FinishedActivityHeuristic => "the latest observed activity is a completed result",
        _ => "completion is unavailable"
    };

    private void SetDurationUnavailable(string tooltip)
    {
        SetProperty(
            ref _durationSortSeconds,
            double.PositiveInfinity,
            nameof(DurationSortSeconds));
        SetText("N/A", ref _durationText, nameof(DurationText));
        SetText(tooltip, ref _durationTooltip, nameof(DurationTooltip));
    }

    private OpenCodeMessageDuration? MessageDurationFor(
        ForgeJobSnapshot snapshot,
        LiveActivityHistoryEntry entry)
    {
        if (string.IsNullOrEmpty(_messageDurationSessionId) ||
            !string.Equals(
                _messageDurationSessionId,
                snapshot.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                entry.SessionId,
                snapshot.SessionId,
                StringComparison.Ordinal) ||
            !entry.Kind.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            !_messageDurations.TryGetValue(entry.MessageId, out var duration) ||
            !string.Equals(duration.MessageId, entry.MessageId, StringComparison.Ordinal))
        {
            return null;
        }

        return duration;
    }

    private void UpdateLiveActivityHistory(ForgeJobSnapshot snapshot)
    {
        var previousIdentity = _selectedActivity is { } previous
            ? LiveActivitySelectionIdentity.FromEntry(previous.Entry)
            : (LiveActivitySelectionIdentity?)null;
        var entries = LiveActivityHistoryPolicy.Order(
            snapshot.LiveActivity.History.Where(entry =>
                string.Equals(entry.JobId, snapshot.JobId, StringComparison.Ordinal)));
        var doing = snapshot.LiveActivity.Doing;
        // Resolve the job-level value once from this snapshot and reuse it for
        // every row; rows still prefer their own exact/report-local value.
        var jobReportedStatus =
            ReportedStatusPresentation.ResolveJobReportedStatus(snapshot);
        var rows = entries
            .Select(entry => new LiveActivityHistoryRowViewModel(
                entry,
                snapshot.ReportedStatus,
                _monitoringActive && MatchesCurrentActiveTarget(snapshot, doing, entry),
                jobReportedStatus,
                MessageDurationFor(snapshot, entry)))
            .ToArray();
        _liveActivityHistory = rows;
        // Reset the ItemsSource before restoring selection so a DataGrid that
        // clears SelectedItem on a collection reset cannot erase this job's
        // independent activity choice.
        OnPropertyChanged(nameof(LiveActivityHistory));
        OnPropertyChanged(nameof(HasLiveActivityHistory));

        var selected = previousIdentity is { } identity
            ? rows.FirstOrDefault(row =>
                LiveActivitySelectionIdentity.FromEntry(row.Entry).Equals(identity))
            : null;
        selected ??= rows.FirstOrDefault();
        if (!ReferenceEquals(_selectedActivity, selected))
        {
            SetSelectedActivityValue(selected);
        }
    }

    /// <summary>
    /// The spinner is defensible only for the exact current Doing record from
    /// the successful bounded read while Forge metadata explicitly reports
    /// RUNNING. BLOCKED, UNKNOWN, and stale/carried-only rows show no spinner.
    /// </summary>
    private static bool MatchesCurrentActiveTarget(
        ForgeJobSnapshot snapshot,
        ActivityExcerpt? doing,
        LiveActivityHistoryEntry entry)
    {
        if (snapshot.Status != ForgeJobStatus.Running || doing is null)
        {
            return false;
        }

        if (!string.Equals(doing.JobId, entry.JobId, StringComparison.Ordinal) ||
            !string.Equals(doing.SessionId, entry.SessionId, StringComparison.Ordinal) ||
            !string.Equals(doing.MessageId, entry.MessageId, StringComparison.Ordinal) ||
            !string.Equals(doing.PartId, entry.PartId, StringComparison.Ordinal) ||
            !string.Equals(doing.SourcePath, entry.SourcePath, StringComparison.Ordinal) ||
            doing.ByteOffset != entry.ByteOffset ||
            !string.Equals(
                doing.RecordDigest,
                entry.RecordDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An explicit same-part tool end or a step-finish bound to this exact
        // text record is observed terminal evidence. A step-finish later in
        // physical source order also stops the spinner even when the result it
        // describes belongs to another part; an out-of-order terminal event
        // must not be resurrected by timestamps or display order. Never infer
        // completion from text.
        if (snapshot.LiveActivity.DoneEventByteOffset is { } terminalOffset &&
            terminalOffset >= entry.ByteOffset)
        {
            return false;
        }

        if (entry.ObservedDuration is not null &&
            entry.DurationStartUnixMilliseconds is not null &&
            entry.DurationEndUnixMilliseconds is not null)
        {
            return false;
        }

        return snapshot.LiveActivity.Done is not { } done ||
               !string.Equals(done.JobId, entry.JobId, StringComparison.Ordinal) ||
               !string.Equals(done.SessionId, entry.SessionId, StringComparison.Ordinal) ||
               !string.Equals(done.MessageId, entry.MessageId, StringComparison.Ordinal) ||
               !string.Equals(done.PartId, entry.PartId, StringComparison.Ordinal) ||
               !string.Equals(done.SourcePath, entry.SourcePath, StringComparison.Ordinal) ||
               done.ByteOffset != entry.ByteOffset ||
               !string.Equals(
                   done.RecordDigest,
                   entry.RecordDigest,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void MergeEvents(IReadOnlyList<MonitoringEvent> incoming)
    {
        // Shared policy: missing timestamps are source-order barriers, known
        // segments sort newest-first, and equal timestamps keep source order.
        // ObservedAt and EventKey are deliberately not used as tie-breakers.
        var desired = TimelineOrdering
            .OrderForDisplay(
                incoming,
                MaxTimelineEvents,
                Snapshot.HasObservedAmbiguousTimestamps)
            .ToList();

        var desiredKeys = desired
            .Select(item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);

        for (var index = RecentEvents.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(RecentEvents[index].EventKey))
            {
                RecentEvents.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var currentIndex = -1;
            for (var candidate = index; candidate < RecentEvents.Count; candidate++)
            {
                if (string.Equals(
                        RecentEvents[candidate].EventKey,
                        item.EventKey,
                        StringComparison.Ordinal))
                {
                    currentIndex = candidate;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                RecentEvents.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                RecentEvents.Move(currentIndex, index);
            }
        }

        while (RecentEvents.Count > desired.Count)
        {
            RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }
    }

    private bool SetText(string value, ref string field, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static bool TryGetProjectLeaf(string? path, out string leaf)
    {
        leaf = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            var trimmed = TrimTrailingDirectorySeparators(path);
            if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            var root = Path.GetPathRoot(trimmed);
            if (string.IsNullOrEmpty(root) ||
                string.Equals(
                    TrimTrailingDirectorySeparators(trimmed),
                    TrimTrailingDirectorySeparators(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate is "." or ".." ||
                candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            leaf = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string TrimTrailingDirectorySeparators(string path)
    {
        var end = path.Length;
        while (end > 0 &&
               (path[end - 1] == Path.DirectorySeparatorChar ||
                path[end - 1] == Path.AltDirectorySeparatorChar))
        {
            end--;
        }

        return path[..end];
    }

    private static long? StartTicks(DateTimeOffset? startedAt) =>
        startedAt?.ToUniversalTime().Ticks;

    private static string FormatJobDisplay(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return jobId;
        }

        var elementStarts = StringInfo.ParseCombiningCharacters(jobId);
        return elementStarts.Length <= 4
            ? jobId
            : "…" + jobId[elementStarts[^4]..];
    }

    private static string FormatDurationDetail(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }
}
