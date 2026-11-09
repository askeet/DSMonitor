using DSMonitor.Models;

namespace DSMonitor.Services;

/// <summary>
/// Bounded, partition-independent retention state for one job timeline.
/// It keeps the two candidate sets required by the documented policy:
/// the most recent <c>capacity</c> source events and the <c>capacity</c>
/// newest timestamped events. Keeping both means an all-known history can
/// still be reduced to its newest events, while a missing timestamp observed
/// at any later poll cannot reveal that source events needed by the
/// source-window policy were already discarded.
///
/// Memory is bounded by twice the timeline capacity (plus the caller's
/// deduplication set). Events are never invented or reordered here.
/// </summary>
public sealed class TimelineRetentionBuffer
{
    private readonly int _capacity;
    private readonly List<MonitoringEvent> _sourceWindow = new();
    private readonly List<MonitoringEvent> _newestTimestampedCandidates = new();
    private readonly HashSet<string> _retainedKeys = new(StringComparer.Ordinal);

    public TimelineRetentionBuffer(int capacity = TimelineOrdering.DefaultCapacity)
    {
        _capacity = capacity;
    }

    /// <summary>
    /// True once any event without a timestamp has been observed. It remains
    /// true for the lifetime of this buffer so later barrier eviction cannot
    /// switch the history back to timestamp-only retention.
    /// </summary>
    public bool HasObservedAmbiguousTimestamps { get; private set; }

    /// <summary>
    /// Upper bound on events held by this buffer. Callers can use this to
    /// verify the per-job memory bound.
    /// </summary>
    public int BoundedEventCount => _sourceWindow.Count + _newestTimestampedCandidates.Count;

    public int Capacity => _capacity;

    /// <summary>
    /// Restores the two bounded candidate windows and the historical ambiguity
    /// state from committed storage. Both windows are necessary: restoring only
    /// the currently displayed window changes later retention when a missing
    /// timestamp arrives after restart.
    /// </summary>
    public void Restore(
        IReadOnlyList<MonitoringEvent> sourceWindow,
        IReadOnlyList<MonitoringEvent> newestTimestampedWindow,
        bool hasObservedAmbiguousTimestamps)
    {
        ArgumentNullException.ThrowIfNull(sourceWindow);
        ArgumentNullException.ThrowIfNull(newestTimestampedWindow);

        _sourceWindow.Clear();
        _newestTimestampedCandidates.Clear();
        HasObservedAmbiguousTimestamps = hasObservedAmbiguousTimestamps;

        if (_capacity <= 0)
        {
            _retainedKeys.Clear();
            return;
        }

        AppendDistinct(
            _sourceWindow,
            sourceWindow.Skip(Math.Max(0, sourceWindow.Count - _capacity)));
        AppendDistinct(
            _newestTimestampedCandidates,
            newestTimestampedWindow
                .Where(item => item.Timestamp is not null)
                .Take(_capacity));

        TrimToCapacity(_sourceWindow);
        TrimToCapacity(_newestTimestampedCandidates);
        RebuildRetainedKeys();
    }

    private static void AppendDistinct(
        List<MonitoringEvent> destination,
        IEnumerable<MonitoringEvent> items)
    {
        var seen = destination
            .Select(item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(item.EventKey))
            {
                destination.Add(item);
            }
        }
    }

    /// <summary>
    /// Adds unique newly accepted events in source order. Duplicate filtering
    /// remains the caller's responsibility; this type only applies retention.
    /// </summary>
    public void AddRange(IReadOnlyList<MonitoringEvent> newEventsInSourceOrder)
    {
        ArgumentNullException.ThrowIfNull(newEventsInSourceOrder);
        if (newEventsInSourceOrder.Count == 0 || _capacity <= 0)
        {
            return;
        }

        foreach (var item in newEventsInSourceOrder)
        {
            if (item.Timestamp is null)
            {
                HasObservedAmbiguousTimestamps = true;
                break;
            }
        }

        _sourceWindow.AddRange(newEventsInSourceOrder);
        TrimToCapacity(_sourceWindow);

        // The newest candidate set must include any event that could be among
        // the newest `capacity` timestamps of the whole history. A stable
        // descending sort over source-ordered indices keeps the earlier source
        // event for equal timestamps, then the result is restored to source
        // order for deterministic snapshot output.
        var combined = new List<MonitoringEvent>(
            _newestTimestampedCandidates.Count + newEventsInSourceOrder.Count);
        combined.AddRange(_newestTimestampedCandidates);
        combined.AddRange(newEventsInSourceOrder);

        var knownIndices = new List<int>(combined.Count);
        for (var index = 0; index < combined.Count; index++)
        {
            if (combined[index].Timestamp is not null)
            {
                knownIndices.Add(index);
            }
        }

        var retainedIndices = knownIndices
            .OrderByDescending(index => combined[index].Timestamp)
            .Take(_capacity)
            .OrderBy(index => index)
            .ToArray();

        _newestTimestampedCandidates.Clear();
        foreach (var index in retainedIndices)
        {
            _newestTimestampedCandidates.Add(combined[index]);
        }

        RebuildRetainedKeys();
    }

    /// <summary>
    /// True when the key belongs to an event that is still retained in either
    /// bounded candidate window. Callers use this in addition to their recent
    /// dedupe window so replaying a line whose original occurrence is still
    /// visible cannot add a duplicate timeline entry after the bounded recent
    /// dedupe key was evicted.
    /// </summary>
    public bool ContainsRetainedKey(string eventKey) =>
        _retainedKeys.Contains(eventKey);

    /// <summary>
    /// Returns the retained events in source order using the documented mode:
    /// source-window mode once a missing timestamp has ever been observed,
    /// otherwise newest-timestamp mode.
    /// </summary>
    public IReadOnlyList<MonitoringEvent> RetainSourceOrder()
    {
        if (_capacity <= 0)
        {
            return Array.Empty<MonitoringEvent>();
        }

        var selected = HasObservedAmbiguousTimestamps
            ? _sourceWindow
            : _newestTimestampedCandidates;
        return selected.ToArray();
    }

    private void TrimToCapacity(List<MonitoringEvent> events)
    {
        var excess = events.Count - _capacity;
        if (excess > 0)
        {
            events.RemoveRange(0, excess);
        }
    }

    private void RebuildRetainedKeys()
    {
        _retainedKeys.Clear();
        foreach (var item in _sourceWindow)
        {
            _retainedKeys.Add(item.EventKey);
        }

        foreach (var item in _newestTimestampedCandidates)
        {
            _retainedKeys.Add(item.EventKey);
        }
    }
}
