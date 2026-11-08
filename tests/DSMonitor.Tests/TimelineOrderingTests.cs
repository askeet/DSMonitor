using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class TimelineOrderingTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [DataTestMethod]
    [DataRow(500)]
    [DataRow(501)]
    [DataRow(800)]
    public void OrderForDisplay_AllKnown_RetainsNewest500RegardlessArrival(int eventCount)
    {
        var source = Enumerable.Range(0, eventCount)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                Epoch.AddSeconds(eventCount - index).ToUnixTimeMilliseconds()))
            .ToArray();

        var result = TimelineOrdering.OrderForDisplay(source);

        Assert.AreEqual(Math.Min(eventCount, 500), result.Count);
        Assert.AreEqual(Epoch.AddSeconds(eventCount).ToUnixTimeMilliseconds(),
            result[0].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(
            Epoch.AddSeconds(eventCount - result.Count + 1).ToUnixTimeMilliseconds(),
            result[^1].Timestamp!.Value.ToUnixTimeMilliseconds());
        for (var index = 1; index < result.Count; index++)
        {
            Assert.IsTrue(result[index - 1].Timestamp >= result[index].Timestamp);
        }
    }

    [TestMethod]
    public void OrderForDisplay_AllKnown_ReverseArrivalSortsNewestFirst()
    {
        var source = new[]
        {
            Event("oldest", Epoch.ToUnixTimeMilliseconds()),
            Event("middle", Epoch.AddSeconds(1).ToUnixTimeMilliseconds()),
            Event("newest", Epoch.AddSeconds(2).ToUnixTimeMilliseconds())
        };

        var result = TimelineOrdering.OrderForDisplay(source);

        CollectionAssert.AreEqual(
            new[] { "newest", "middle", "oldest" },
            result.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void OrderForDisplay_AllKnown_EqualTimestampsPreserveSourceOrder()
    {
        var timestamp = Epoch.ToUnixTimeMilliseconds();
        var source = new[]
        {
            Event("z-first", timestamp),
            Event("a-second", timestamp),
            Event("m-third", timestamp)
        };

        var result = TimelineOrdering.OrderForDisplay(source);

        CollectionAssert.AreEqual(
            new[] { "z-first", "a-second", "m-third" },
            result.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void OrderForDisplay_MixedMissingTimestamp_IsSourceOrderBarrier()
    {
        var source = new[]
        {
            Event("missing-first", null),
            Event("known-older", Epoch.ToUnixTimeMilliseconds()),
            Event("known-newer", Epoch.AddSeconds(5).ToUnixTimeMilliseconds())
        };

        var result = TimelineOrdering.OrderForDisplay(source);

        CollectionAssert.AreEqual(
            new[] { "missing-first", "known-newer", "known-older" },
            result.Select(item => item.EventKey).ToArray());
        Assert.IsNull(result[0].Timestamp);
    }

    [TestMethod]
    public void OrderForDisplay_MixedKnownSegments_DoNotCrossBarriers()
    {
        var source = new[]
        {
            Event("known-a", Epoch.AddSeconds(3).ToUnixTimeMilliseconds()),
            Event("known-b", Epoch.AddSeconds(1).ToUnixTimeMilliseconds()),
            Event("barrier", null),
            Event("known-c", Epoch.AddSeconds(2).ToUnixTimeMilliseconds()),
            Event("known-d", Epoch.AddSeconds(4).ToUnixTimeMilliseconds())
        };

        var result = TimelineOrdering.OrderForDisplay(source);

        CollectionAssert.AreEqual(
            new[] { "known-a", "known-b", "barrier", "known-d", "known-c" },
            result.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void OrderForDisplay_AllMissing_RetainsSourceWindowInSourceOrder()
    {
        var source = Enumerable.Range(0, 800)
            .Select(index => Event("event-" + index.ToString("0000"), null))
            .ToArray();

        var result = TimelineOrdering.OrderForDisplay(source);

        Assert.AreEqual(500, result.Count);
        Assert.AreEqual("event-0300", result[0].EventKey);
        Assert.AreEqual("event-0799", result[^1].EventKey);
        Assert.IsTrue(result.All(item => item.Timestamp is null));
    }

    [TestMethod]
    public void OrderForDisplay_ForcedSourceWindow_KeepsMixedModeAfterBarrierEviction()
    {
        var source = Enumerable.Range(0, 502)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                Epoch.AddSeconds(502 - index).ToUnixTimeMilliseconds()))
            .ToArray();

        var withoutMode = TimelineOrdering.OrderForDisplay(source);
        var withPersistentMode = TimelineOrdering.OrderForDisplay(
            source,
            forceSourceWindow: true);

        Assert.AreEqual(500, withoutMode.Count);
        Assert.AreEqual("event-0499", withoutMode[^1].EventKey);
        Assert.AreEqual(500, withPersistentMode.Count);
        Assert.AreEqual("event-0002", withPersistentMode[0].EventKey);
        Assert.AreEqual("event-0501", withPersistentMode[^1].EventKey);
    }

    [TestMethod]
    public void OrderForDisplay_RepeatedCalls_AreDeterministic()
    {
        var source = Enumerable.Range(0, 800)
            .Select(index => index == 400
                ? Event("barrier", null)
                : Event(
                    "event-" + index.ToString("0000"),
                    Epoch.AddSeconds((index * 37) % 800).ToUnixTimeMilliseconds()))
            .ToArray();

        var first = TimelineOrdering.OrderForDisplay(source);
        var second = TimelineOrdering.OrderForDisplay(source);

        CollectionAssert.AreEqual(
            first.Select(item => item.EventKey).ToArray(),
            second.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void RetainSourceOrder_AllKnown_SelectsNewestButKeepsSourceOrder()
    {
        var source = Enumerable.Range(0, 800)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                Epoch.AddSeconds((index * 37) % 800).ToUnixTimeMilliseconds()))
            .ToArray();
        var expectedKeys = source
            .OrderByDescending(item => item.Timestamp)
            .Take(500)
            .Select(item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);

        var retained = TimelineOrdering.RetainSourceOrder(source);

        Assert.AreEqual(500, retained.Count);
        Assert.IsTrue(retained.Select(item => item.EventKey).SequenceEqual(
            source.Select(item => item.EventKey).Where(expectedKeys.Contains)));
    }

    [TestMethod]
    public void RetainSourceOrder_Mixed_KeepsBarrierInSourceWindow()
    {
        var source = Enumerable.Range(0, 800)
            .Select(index => index == 700
                ? Event("barrier", null)
                : Event(
                    "event-" + index.ToString("0000"),
                    Epoch.AddSeconds(index).ToUnixTimeMilliseconds()))
            .ToArray();

        var retained = TimelineOrdering.RetainSourceOrder(source);

        Assert.AreEqual(500, retained.Count);
        Assert.AreEqual("event-0300", retained[0].EventKey);
        Assert.AreEqual("barrier", retained[400].EventKey);
        Assert.AreEqual("event-0799", retained[^1].EventKey);
    }

    private static MonitoringEvent Event(string key, long? timestamp) =>
        new()
        {
            EventKey = key,
            JobId = "job_order_0001",
            SessionId = "ses_order_0001",
            Timestamp = timestamp is null
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value),
            ObservedAt = Epoch,
            Category = MonitoringEventCategory.Tool,
            Severity = MonitoringEventSeverity.Info,
            Summary = key
        };
}
