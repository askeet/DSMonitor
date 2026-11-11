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

    /// <summary>Unverified agent-reported text with the complete untruncated value.</summary>
    public string ReportedStatusTooltip => ReportedStatusPresentation.ToTooltip(Snapshot.ReportedStatus);

    /// <summary>
    /// Table cell value. When an unambiguous reported status is unavailable,
    /// the job's own latest observed activity is shown with a bullet prefix
    /// instead of a bare N/A. The activity remains unverified and is never
    /// treated as authoritative status.
    /// </summary>
    public string ReportedStatusCellText
    {
        get
        {
            if (!string.Equals(
                    ReportedStatusText,
                    ReportedStatusPresentation.Unavailable,
                    StringComparison.Ordinal))
            {
                return ReportedStatusText;
            }

            return TryGetJobActivity(out var activity)
                ? "• " + activity.Text
                : ReportedStatusPresentation.Unavailable;
        }
    }

    /// <summary>
    /// Table cell tooltip. Reported status keeps its unverified explanation; an
    /// activity fallback keeps the complete observed text and is labeled as
    /// unverified job activity rather than status.
    /// </summary>
    public string ReportedStatusCellTooltip
    {
        get
        {
            if (!string.Equals(
                    ReportedStatusText,
                    ReportedStatusPresentation.Unavailable,
                    StringComparison.Ordinal))
            {
                return ReportedStatusTooltip;
            }

            return TryGetJobActivity(out var activity)
                ? activity.Text +
                  " — unverified observed job activity; not authoritative Forge execution status."
                : ReportedStatusTooltip;
        }
    }

    private bool TryGetJobActivity(out ActivityExcerpt activity)
    {
        if (Snapshot.LiveActivity.Doing is { } doing &&
            string.Equals(doing.JobId, Snapshot.JobId, StringComparison.Ordinal))
        {
            activity = doing;
            return true;
        }

        activity = null!;
        return false;
    }

    public bool IsActive => _status.IsActive();
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
        RefreshElapsed(now);
        OnPropertyChanged(nameof(Snapshot));
        foreach (var property in new[] { nameof(ChatText), nameof(TaskText), nameof(DoingText),
                     nameof(DoneText), nameof(DoingProvenance), nameof(DoneProvenance),
                     nameof(LiveActivityNotice), nameof(LiveUpdatedText), nameof(StatusProvenance),
                     nameof(ReportedStatusText), nameof(ReportedStatusTooltip),
                     nameof(ReportedStatusCellText), nameof(ReportedStatusCellTooltip) })
            OnPropertyChanged(property);
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
        if (_durationBasis != JobDurationBasis.Live ||
            _durationStartedAt is not { } started)
        {
            return;
        }

        SetLiveDuration(started, now);
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
                _durationBasis = JobDurationBasis.Live;
                SetLiveDuration(started, now);
                return;

            default:
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
                    SetObservedSpan(snapshot, started);
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

    private void SetObservedSpan(ForgeJobSnapshot snapshot, DateTimeOffset started)
    {
        _durationBasis = JobDurationBasis.ObservedSpan;
        if (snapshot.LastActivity is not { } lastActivity)
        {
            SetDurationUnavailable(
                "N/A — started or last activity is unavailable; duration is not inferred.");
            return;
        }

        if (lastActivity < started)
        {
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
