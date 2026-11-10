using System.Collections.ObjectModel;
using System.IO;
using DSMonitor.Models;
using DSMonitor.Services;

namespace DSMonitor.ViewModels;

public sealed class JobRowViewModel : ObservableObject
{
    public const int MaxTimelineEvents = TimelineOrdering.DefaultCapacity;

    private ForgeJobStatus _status = ForgeJobStatus.Unknown;
    private string _projectText = "—";
    private string _projectTooltip = string.Empty;
    private string _modelText = "—";
    private string _startedText = "—";
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
    public ForgeJobSnapshot Snapshot { get; private set; } = null!;
    public DateTimeOffset StartedSort { get; private set; } = DateTimeOffset.MinValue;
    public string StatusText => _status.ToDisplay();
    public bool IsActive => _status.IsActive();
    public string ProjectText => _projectText;
    public string ProjectTooltip => _projectTooltip;
    public string ModelText => _modelText;
    public string StartedText => _startedText;
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

        if (_status != snapshot.Status)
        {
            _status = snapshot.Status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ForgeStatusLabel));
        }

        SetText(snapshot.Metadata.Directory is { Length: > 0 } directory
            ? directory
            : "N/A", ref _projectText, nameof(ProjectText));
        SetText(snapshot.Metadata.Directory ?? string.Empty, ref _projectTooltip, nameof(ProjectTooltip));
        SetText(snapshot.Metadata.Model ?? "—", ref _modelText, nameof(ModelText));
        SetText(
            snapshot.Metadata.Started is { } started
                ? started.ToLocalTime().ToString("MMM dd, HH:mm")
                : "—",
            ref _startedText,
            nameof(StartedText));
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
                     nameof(LiveActivityNotice), nameof(LiveUpdatedText), nameof(StatusProvenance) })
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

    public void RefreshElapsed(DateTimeOffset now)
    {
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

    private static string LeafDirectory(string path)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
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
