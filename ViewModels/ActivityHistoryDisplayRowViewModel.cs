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

/// <summary>Flat DataGrid row wrapper for a hierarchical Message/Parts view.</summary>
public sealed class ActivityHistoryDisplayRowViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _hasVisibleChildren;

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
    public string StartedText => Activity.StartedText;
    public string StartedTooltip => Activity.StartedTooltip;
    public string DurationText => Activity.DurationText;
    public string DurationTooltip => Activity.DurationTooltip;
    public double DurationSortSeconds => Activity.DurationSortSeconds;
    public string ReportedStatusText => Activity.ReportedStatusText;
    public string ReportedStatusTooltip => Activity.ReportedStatusTooltip;
    public bool IsCurrentActiveTarget => Activity.IsCurrentActiveTarget;
    public ActivityHistoryMessageGroup? Group { get; }
    public bool IsMessage { get; }
    public bool IsChild { get; }
    public bool IsOther { get; }
    public bool HasChildren => IsMessage && Group?.Children.Count > 0;
    public bool HasVisibleChildren => _hasVisibleChildren;
    public string TypeGlyph => Activity.IsTextActivity ? "▤"
        : Activity.IsToolActivity ? "⚒"
        : Activity.IsStepActivity ? "☷" : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (HasChildren)
            {
                SetProperty(ref _isExpanded, value);
            }
        }
    }

    public static IReadOnlyList<ActivityHistoryDisplayRowViewModel> Project(
        IEnumerable<ActivityHistoryMessageGroup> groups,
        IEnumerable<LiveActivityHistoryRowViewModel> otherRows,
        bool showText,
        bool showTools,
        bool showSteps,
        bool showOther)
    {
        var top = groups.Select(group => group.DisplayRow);
        var others = showOther
            ? otherRows.Select(Other)
            : Enumerable.Empty<ActivityHistoryDisplayRowViewModel>();
        var rows = top.Concat(others)
            .OrderBy(row => row.Activity.StartedSortTimestamp is null)
            .ThenByDescending(row => row.Activity.StartedSortTimestamp)
            .ThenByDescending(row => row.Activity.Entry.ByteOffset)
            .ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var parent = rows[i];
            parent.SetProperty(ref parent._hasVisibleChildren, parent.Group?.Children.Any(child =>
                (showText && child.IsTextActivity) ||
                (showTools && child.IsToolActivity) ||
                (showSteps && child.IsStepActivity)) == true, nameof(HasVisibleChildren));
            if (!parent.IsExpanded || parent.Group is null)
            {
                continue;
            }

            var children = parent.Group.Children
                .Where(child => (showText && child.IsTextActivity) ||
                                (showTools && child.IsToolActivity) ||
                                (showSteps && child.IsStepActivity))
                .OrderBy(child => child.StartedSortTimestamp is null)
                .ThenByDescending(child => child.StartedSortTimestamp)
                .ThenByDescending(child => child.Entry.ByteOffset)
                .Select(Child)
                .ToArray();
            rows.InsertRange(++i, children);
            i += children.Length - 1;
        }

        return rows;
    }

    public static ActivityHistoryDisplayRowViewModel Message(ActivityHistoryMessageGroup group) =>
        new(group.Message, true, false, false, group);

    public static ActivityHistoryDisplayRowViewModel Child(LiveActivityHistoryRowViewModel activity) =>
        new(activity, false, true, false, null);

    public static ActivityHistoryDisplayRowViewModel Other(LiveActivityHistoryRowViewModel activity) =>
        new(activity, false, false, true, null);
}
