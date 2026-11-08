using DSMonitor.Models;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class JobRowTimelineOrderingTests
{
    [TestMethod]
    public void Update_DistinctTimestampsOutOfOrder_SortsNewestFirst()
    {
        var row = CreateRow(
            Event("older", 1_800_000_001_000),
            Event("newest", 1_800_000_003_000),
            Event("middle", 1_800_000_002_000));

        CollectionAssert.AreEqual(
            new[] { "newest", "middle", "older" },
            row.RecentEvents.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void Update_EqualTimestamps_PreservesSourceOrderWithoutKeyTieBreak()
    {
        var timestamp = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var row = CreateRow(
            Event("z-source-first", timestamp.ToUnixTimeMilliseconds()),
            Event("a-source-second", timestamp.ToUnixTimeMilliseconds()),
            Event("m-source-third", timestamp.ToUnixTimeMilliseconds()));

        CollectionAssert.AreEqual(
            new[] { "z-source-first", "a-source-second", "m-source-third" },
            row.RecentEvents.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void Update_MissingTimestampsAcrossBatches_PreservesSourceOrder()
    {
        var row = CreateRow(
            Event("z-first", null),
            Event("a-second", null));

        row.Update(
            Snapshot(
                Event("z-first", null),
                Event("a-second", null),
                Event("m-third", null),
                Event("b-fourth", null)),
            DateTimeOffset.UtcNow);

        CollectionAssert.AreEqual(
            new[] { "z-first", "a-second", "m-third", "b-fourth" },
            row.RecentEvents.Select(item => item.EventKey).ToArray());
    }

    [TestMethod]
    public void Update_EqualTimestampsAcrossBatches_PreservesCombinedSourceOrder()
    {
        var timestamp = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var row = CreateRow(
            Event("z-first", timestamp),
            Event("a-second", timestamp));

        row.Update(
            Snapshot(
                Event("z-first", timestamp),
                Event("a-second", timestamp),
                Event("m-third", timestamp),
                Event("b-fourth", timestamp)),
            DateTimeOffset.UtcNow);

        CollectionAssert.AreEqual(
            new[] { "z-first", "a-second", "m-third", "b-fourth" },
            row.RecentEvents.Select(item => item.EventKey).ToArray());
    }

    [DataTestMethod]
    [DataRow(500)]
    [DataRow(501)]
    [DataRow(800)]
    public void Update_Retention_KeepsNewestFiveHundred(int eventCount)
    {
        var events = Enumerable.Range(0, eventCount)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)
                    .AddSeconds(index)
                    .ToUnixTimeMilliseconds()))
            .ToArray();

        var row = CreateRow(events);

        Assert.AreEqual(JobRowViewModel.MaxTimelineEvents, row.RecentEvents.Count);
        Assert.AreEqual("event-" + (eventCount - 1).ToString("0000"), row.RecentEvents[0].EventKey);
        Assert.AreEqual("event-" + (eventCount - 500).ToString("0000"), row.RecentEvents[^1].EventKey);
    }

    [TestMethod]
    public void Update_PersistentAmbiguityMode_KeepsSourceWindowAfterBarrierEviction()
    {
        var events = Enumerable.Range(0, 502)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)
                    .AddSeconds(502 - index)
                    .ToUnixTimeMilliseconds()))
            .ToArray();

        var row = new JobRowViewModel(Snapshot(events, hasObservedAmbiguity: true));

        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual("event-0002", row.RecentEvents[0].EventKey);
        Assert.AreEqual("event-0501", row.RecentEvents[^1].EventKey);
    }

    [TestMethod]
    public void Update_RetentionAcrossIncrementalBatches_RemainsOrderedAndDuplicateFree()
    {
        var all = Enumerable.Range(0, 800)
            .Select(index => Event(
                "event-" + index.ToString("0000"),
                new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)
                    .AddSeconds(index)
                    .ToUnixTimeMilliseconds()))
            .ToArray();

        var row = CreateRow(all[..400]);
        row.Update(Snapshot(all[..600]), DateTimeOffset.UtcNow);
        row.Update(Snapshot(all), DateTimeOffset.UtcNow);

        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual(500, row.RecentEvents.Select(item => item.EventKey).Distinct().Count());
        Assert.AreEqual("event-0799", row.RecentEvents[0].EventKey);
        Assert.AreEqual("event-0300", row.RecentEvents[^1].EventKey);
        for (var index = 1; index < row.RecentEvents.Count; index++)
        {
            Assert.IsTrue(
                row.RecentEvents[index - 1].Timestamp >= row.RecentEvents[index].Timestamp,
                "Timeline timestamps must remain newest-first.");
        }
    }

    private static JobRowViewModel CreateRow(params MonitoringEvent[] events) =>
        new(Snapshot(events));

    private static ForgeJobSnapshot Snapshot(params MonitoringEvent[] events) =>
        Snapshot(events, events.Any(item => item.Timestamp is null));

    private static ForgeJobSnapshot Snapshot(
        IReadOnlyList<MonitoringEvent> events,
        bool hasObservedAmbiguity) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = "job_timeline_0001",
                MetadataPath = "test.json"
            },
            Status = ForgeJobStatus.Unknown,
            SessionId = "ses_timeline_0001",
            ObservedAt = DateTimeOffset.UtcNow,
            HasObservedAmbiguousTimestamps = hasObservedAmbiguity,
            Events = events,
            ParsedEventCount = events.Count
        };

    private static MonitoringEvent Event(string key, long? timestamp) =>
        new()
        {
            EventKey = key,
            JobId = "job_timeline_0001",
            SessionId = "ses_timeline_0001",
            Timestamp = timestamp is null
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value),
            ObservedAt = DateTimeOffset.UtcNow,
            Category = MonitoringEventCategory.Tool,
            Severity = MonitoringEventSeverity.Info,
            Summary = key
        };
}
