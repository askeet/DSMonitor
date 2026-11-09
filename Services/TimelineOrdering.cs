using DSMonitor.Models;

namespace DSMonitor.Services;

/// <summary>
/// Deterministic timeline policy shared by the view model and policy tests.
/// Service-side incremental retention is handled by <see cref="TimelineRetentionBuffer"/>,
/// which keeps both the source window and newest-timestamp candidates so a
/// late missing timestamp cannot make the result depend on poll boundaries.
/// Missing timestamps are source-order barriers, not sortable MinValue values.
/// Known timestamp segments sort newest-first; equal timestamps keep their
/// original source order. Retention is bounded and preserves the barrier
/// context needed by later incremental updates.
/// </summary>
public static class TimelineOrdering
{
    public const int DefaultCapacity = 500;

    /// <summary>
    /// Applies retention to a source-ordered event sequence and returns the
    /// retained events in source order. All-known histories keep the newest
    /// timestamped events regardless of arrival position. Histories containing
    /// missing timestamps keep the most recent source window so barriers and
    /// ambiguous events are never reordered or merged around a dropped barrier.
    /// </summary>
    public static IReadOnlyList<MonitoringEvent> RetainSourceOrder(
        IReadOnlyList<MonitoringEvent> sourceOrderedEvents,
        int capacity = DefaultCapacity,
        bool forceSourceWindow = false)
    {
        ArgumentNullException.ThrowIfNull(sourceOrderedEvents);
        if (capacity <= 0)
        {
            return Array.Empty<MonitoringEvent>();
        }

        if (sourceOrderedEvents.Count <= capacity)
        {
            return sourceOrderedEvents.ToArray();
        }

        var hasMissingTimestamp = sourceOrderedEvents.Any(item => item.Timestamp is null);
        if (hasMissingTimestamp || forceSourceWindow)
        {
            // A history that has ever contained ambiguous timestamps keeps the
            // recent source window even after the barrier leaves the window.
            return sourceOrderedEvents
                .Skip(sourceOrderedEvents.Count - capacity)
                .ToArray();
        }

        var retainedKeys = sourceOrderedEvents
            .OrderByDescending(item => item.Timestamp)
            .Take(capacity)
            .Select(item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);

        return sourceOrderedEvents
            .Where(item => retainedKeys.Contains(item.EventKey))
            .ToArray();
    }

    /// <summary>
    /// Returns the bounded, newest-first display sequence. Missing timestamps
    /// remain at their source-order barrier positions; each adjacent known
    /// segment is sorted newest-first with a stable equal-timestamp order.
    /// </summary>
    public static IReadOnlyList<MonitoringEvent> OrderForDisplay(
        IReadOnlyList<MonitoringEvent> sourceOrderedEvents,
        int capacity = DefaultCapacity,
        bool forceSourceWindow = false)
    {
        var retained = RetainSourceOrder(sourceOrderedEvents, capacity, forceSourceWindow);
        var ordered = new List<MonitoringEvent>(retained.Count);
        var knownSegment = new List<MonitoringEvent>();

        foreach (var item in retained)
        {
            if (item.Timestamp is null)
            {
                FlushKnownSegment(knownSegment, ordered);
                ordered.Add(item);
                continue;
            }

            knownSegment.Add(item);
        }

        FlushKnownSegment(knownSegment, ordered);
        return ordered;
    }

    private static void FlushKnownSegment(
        List<MonitoringEvent> segment,
        List<MonitoringEvent> destination)
    {
        if (segment.Count == 0)
        {
            return;
        }

        // OrderByDescending is stable: equal timestamps keep source order.
        destination.AddRange(segment.OrderByDescending(item => item.Timestamp));
        segment.Clear();
    }
}
