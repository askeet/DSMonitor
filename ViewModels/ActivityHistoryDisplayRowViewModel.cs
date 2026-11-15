using System.ComponentModel;
using DSMonitor.Models;

namespace DSMonitor.ViewModels;

/// <summary>
/// Explicit presentation contract for one Message and the activity rows that a
/// later history projection can safely associate with it. The UI never derives
/// membership from timestamps, paths, or shared session/message text.
/// </summary>
public sealed class ActivityHistoryMessageGroup
{
    private ActivityHistoryDisplayRowViewModel? _displayRow;
    public ActivityHistoryMessageGroup(
        LiveActivityHistoryRowViewModel message,
        IEnumerable<LiveActivityHistoryRowViewModel>? children = null)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Children = (children ?? Array.Empty<LiveActivityHistoryRowViewModel>()).ToArray();
    }

    public LiveActivityHistoryRowViewModel Message { get; }
    public IReadOnlyList<LiveActivityHistoryRowViewModel> Children { get; }
    internal ActivityHistoryDisplayRowViewModel DisplayRow =>
        _displayRow ??= ActivityHistoryDisplayRowViewModel.Message(this);
}

/// <summary>
/// Stable grid-row identity. A parent message row and a child part row that
/// carry the same physical activity entry remain distinct rows because
/// <paramref name="IsMessage"/> is part of the key. The key is presentation-only
/// and never replaces the entry's own job/source/session/message/part identity.
/// </summary>
public readonly record struct ActivityHistoryRowIdentity(
    string JobId,
    string SourcePath,
    string SessionId,
    string MessageId,
    string PartId,
    bool IsMessage)
{
    /// <summary>
    /// Parent identity is the exact message identity, not the current
    /// representative part. A representative change therefore keeps the parent
    /// selected while the physical part identity of each child stays distinct.
    /// </summary>
    public static ActivityHistoryRowIdentity ForMessage(
        string jobId,
        string sourcePath,
        string sessionId,
        string messageId) =>
        new(jobId, sourcePath, sessionId, messageId, string.Empty, true);

    public static ActivityHistoryRowIdentity ForPart(LiveActivityHistoryEntry entry) =>
        new(
            entry.JobId,
            entry.SourcePath,
            entry.SessionId,
            entry.MessageId,
            entry.PartId,
            false);
}

/// <summary>Flat DataGrid row wrapper for a hierarchical Message/Parts view.</summary>
public sealed class ActivityHistoryDisplayRowViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _hasVisibleChildren;
    private bool _isStartMarker;

    private ActivityHistoryDisplayRowViewModel(
        LiveActivityHistoryRowViewModel activity,
        bool isMessage,
        bool isChild,
        bool isOther,
        ActivityHistoryMessageGroup? group)
    {
        Activity = activity;
        IsMessage = isMessage;
        IsChild = isChild;
        IsOther = isOther;
        Group = group;
    }

    public LiveActivityHistoryRowViewModel Activity { get; }

    /// <summary>
    /// Distinct parent/child selection identity. A parent and a child copy of
    /// the same physical text part differ by <see cref="IsMessage"/>.
    /// </summary>
    public ActivityHistoryRowIdentity Key => IsMessage
        ? ActivityHistoryRowIdentity.ForMessage(
            Activity.Entry.JobId,
            Activity.Entry.SourcePath,
            Activity.Entry.SessionId,
            Activity.Entry.MessageId)
        : ActivityHistoryRowIdentity.ForPart(Activity.Entry);

    public string StartedText => IsMessage && IsExpanded && Activity.MessageDuration is { } timing
        ? StartedDisplayFormatter.Format(timing.Completed)
        : IsStartMarker
            ? Activity.MessageDuration is { } messageTiming
                ? StartedDisplayFormatter.Format(messageTiming.Created)
                : string.Empty
            : Activity.StartedText;
    public string StartedTooltip => IsMessage && Activity.MessageDuration is { } timing
        ? $"Message {(IsExpanded ? "finished" : "started")} " +
          $"{(IsExpanded ? timing.Completed : timing.Created):O}. " +
          $"Verified OpenCode assistant message; job {Activity.Entry.JobId}; " +
          $"session {Activity.Entry.SessionId}; message {Activity.Entry.MessageId}."
        : Activity.StartedTooltip;
    public string DurationText => IsStartMarker
        ? string.Empty
        : IsMessage && IsExpanded && Activity.MessageDuration is { } timing
            ? $"-{DurationDisplayFormatter.Format(timing.Duration)}"
            : Activity.DurationText;
    public string DurationTooltip => Activity.DurationTooltip;
    public double DurationSortSeconds => Activity.DurationSortSeconds;
    public string ReportedStatusText
    {
        get
        {
            if (IsStartMarker)
            {
                return "Start of message";
            }

            var status = Activity.ReportedStatusText;
            var includeResponse = IsOther || Activity.IsToolActivity || Activity.IsStepActivity;
            var response = includeResponse ? Activity.Entry.Text?.Trim() : null;
            return string.IsNullOrEmpty(response) ||
                   string.Equals(status, response, StringComparison.Ordinal)
                ? status
                : $"{status} - {response}";
        }
    }
    public string ReportedStatusTooltip => ReportedStatusText;
    public bool IsCurrentActiveTarget => Activity.IsCurrentActiveTarget;
    public ActivityHistoryMessageGroup? Group { get; }
    public bool IsMessage { get; }
    public bool IsChild { get; }
    public bool IsOther { get; }
    public bool IsStartMarker => _isStartMarker;
    public bool HasChildren => IsMessage;
    public bool HasVisibleChildren => _hasVisibleChildren;
    public string TypeGlyph => IsStartMarker ? "▤" : Activity.IsTextActivity ? "▤"
        : Activity.IsToolActivity ? "⚒"
        : Activity.IsStepActivity ? "☷" : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (IsMessage)
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(StartedText));
                    OnPropertyChanged(nameof(StartedTooltip));
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }
    }

    public static IReadOnlyList<ActivityHistoryDisplayRowViewModel> Project(
        IEnumerable<ActivityHistoryMessageGroup> groups,
        IEnumerable<LiveActivityHistoryRowViewModel> otherRows,
        bool showText,
        bool showTools,
        bool showSteps,
        bool showOther,
        ListSortDirection? durationSort = null)
    {
        var top = groups.Select(group => group.DisplayRow);
        var others = showOther
            ? otherRows.Select(Other)
            : Enumerable.Empty<ActivityHistoryDisplayRowViewModel>();
        var blocks = top.Concat(others);
        // Sorting applies to top-level message/Other blocks only. Children are
        // always re-flattened chronologically under their parent, so a block
        // sort never detaches a child from its message.
        var rows = durationSort is { } direction
            ? blocks
                .OrderBy(row => double.IsPositiveInfinity(row.DurationSortSeconds) ? 1 : 0)
                .ThenBy(row => direction == ListSortDirection.Ascending
                    ? row.DurationSortSeconds
                    : -row.DurationSortSeconds)
                .ThenBy(row => row.Activity.StartedSortTimestamp is null)
                .ThenByDescending(row => row.Activity.StartedSortTimestamp)
                .ThenByDescending(row => row.Activity.Entry.ByteOffset)
                .ToList()
            : blocks
                .OrderBy(row => row.Activity.StartedSortTimestamp is null)
                .ThenByDescending(row => row.Activity.StartedSortTimestamp)
                .ThenByDescending(row => row.Activity.Entry.ByteOffset)
                .ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var parent = rows[i];
            parent.SetProperty(ref parent._hasVisibleChildren, parent.IsMessage, nameof(HasVisibleChildren));
            if (!parent.IsExpanded || parent.Group is null)
            {
                continue;
            }

            // Child rows are filtered activity only; the non-activity message
            // boundary marker is always appended, even for an empty group.
            var children = parent.Group.Children
                .Where(child => (showText && child.IsTextActivity) ||
                                (showTools && child.IsToolActivity) ||
                                (showSteps && child.IsStepActivity))
                .OrderBy(child => child.StartedSortTimestamp is null)
                .ThenBy(child => child.StartedSortTimestamp ?? DateTimeOffset.MinValue)
                .ThenBy(child => child.Entry.ByteOffset)
                .ThenBy(child => child.Entry.SessionId, StringComparer.Ordinal)
                .ThenBy(child => child.Entry.MessageId, StringComparer.Ordinal)
                .ThenBy(child => child.Entry.PartId, StringComparer.Ordinal)
                .Select(Child)
                .ToList();
            children.Add(StartMarker(parent.Activity));
            if (children.Count == 1)
            {
                rows.Insert(++i, children[0]);
                continue;
            }
            rows.InsertRange(++i, children);
            i += children.Count - 1;
        }

        return rows;
    }

    private static ActivityHistoryDisplayRowViewModel StartMarker(LiveActivityHistoryRowViewModel activity)
    {
        var marker = new ActivityHistoryDisplayRowViewModel(activity, false, true, false, null)
        {
            _isStartMarker = true
        };
        return marker;
    }

    public static ActivityHistoryDisplayRowViewModel Message(ActivityHistoryMessageGroup group) =>
        new(group.Message, true, false, false, group);

    public static ActivityHistoryDisplayRowViewModel Child(LiveActivityHistoryRowViewModel activity) =>
        new(activity, false, true, false, null);

    public static ActivityHistoryDisplayRowViewModel Other(LiveActivityHistoryRowViewModel activity) =>
        new(activity, false, false, true, null);
}
