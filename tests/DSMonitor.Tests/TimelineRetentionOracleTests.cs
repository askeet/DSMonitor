using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Independent full-history reference implementation of the documented timeline
/// policy. It deliberately does not call production ordering/retention code.
/// </summary>
internal static class ReferenceTimelineOracle
{
    public const int Capacity = 500;

    /// <summary>
    /// Retained events in original source order.
    /// - Histories no longer than the capacity retain everything.
    /// - A history containing any missing timestamp retains the last capacity
    ///   source events (missing timestamps are barriers, not sortable values).
    /// - Otherwise the capacity events with the greatest timestamps are retained;
    ///   equal timestamps prefer the earlier source event (stable source order).
    /// </summary>
    public static IReadOnlyList<MonitoringEvent> Retain(
        IReadOnlyList<MonitoringEvent> fullHistoryInSourceOrder,
        int capacity = Capacity)
    {
        if (capacity <= 0)
        {
            return Array.Empty<MonitoringEvent>();
        }

        if (fullHistoryInSourceOrder.Count <= capacity)
        {
            return fullHistoryInSourceOrder.ToArray();
        }

        if (fullHistoryInSourceOrder.Any(item => item.Timestamp is null))
        {
            return fullHistoryInSourceOrder
                .Skip(fullHistoryInSourceOrder.Count - capacity)
                .ToArray();
        }

        var retainedIndices = Enumerable.Range(0, fullHistoryInSourceOrder.Count)
            .OrderByDescending(index => fullHistoryInSourceOrder[index].Timestamp)
            .Take(capacity)
            .ToHashSet();

        return fullHistoryInSourceOrder
            .Where((_, index) => retainedIndices.Contains(index))
            .ToArray();
    }

    /// <summary>
    /// Visible newest-first order. Missing timestamps stay at their source
    /// barrier positions; each adjacent known segment sorts newest-first and
    /// equal timestamps keep source order.
    /// </summary>
    public static IReadOnlyList<MonitoringEvent> Display(
        IReadOnlyList<MonitoringEvent> fullHistoryInSourceOrder,
        int capacity = Capacity)
    {
        var retained = Retain(fullHistoryInSourceOrder, capacity);
        var ordered = new List<MonitoringEvent>(retained.Count);
        var knownSegment = new List<MonitoringEvent>();

        foreach (var item in retained)
        {
            if (item.Timestamp is null)
            {
                FlushSegment(knownSegment, ordered);
                ordered.Add(item);
                continue;
            }

            knownSegment.Add(item);
        }

        FlushSegment(knownSegment, ordered);
        return ordered;
    }

    private static void FlushSegment(
        List<MonitoringEvent> segment,
        List<MonitoringEvent> destination)
    {
        if (segment.Count == 0)
        {
            return;
        }

        destination.AddRange(segment.OrderByDescending(item => item.Timestamp));
        segment.Clear();
    }
}

[STATestClass]
public sealed class TimelineRetentionOracleTests
{
    private const long BaseTimestamp = 1_900_000_000_000;

    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    /// <summary>
    /// Root defect: the 502-event source [known502, ..., known2, missing] must
    /// retain identical IDs and visible order under one batch, 501+1, 1+501,
    /// 500+2, uneven, and deterministic randomized partitions, including each
    /// intermediate common prefix.
    /// </summary>
    [TestMethod]
    public void ServiceToViewModel_LateMissing502_MatchesOracleAcrossPartitions()
    {
        var lines = BuildLines("firstmissing", 502, 502);
        var fullHistory = ParseLines("job_oracle_reference", lines);
        var partitions = new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["job_late_single_0001"] = new[] { 502 },
            ["job_late_501_1_0001"] = new[] { 501, 1 },
            ["job_late_1_501_0001"] = new[] { 1, 501 },
            ["job_late_500_2_0001"] = new[] { 500, 2 },
            ["job_late_250_252_0001"] = new[] { 250, 252 },
            ["job_late_uneven_0001"] = UnevenPartition(502, 31),
            ["job_late_random_0001"] = RandomPartition(502, 20261109)
        };

        foreach (var (jobId, batches) in partitions)
        {
            Assert.AreEqual(502, batches.Sum());
            _workspace.WriteMetadata(jobId);
            _workspace.WriteLog(jobId, lines.Take(batches[0]).ToArray());
        }

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());

        var seen = partitions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value[0],
            StringComparer.Ordinal);

        harness.WaitFor(
            candidate => candidate.Jobs.Count == partitions.Count &&
                         partitions.All(pair =>
                             candidate.Jobs.Single(job => job.JobId == pair.Key)
                                 .ParsedEventCount == pair.Value[0]),
            TimeSpan.FromSeconds(20));

        foreach (var (jobId, batches) in partitions)
        {
            for (var batchIndex = 1; batchIndex < batches.Length; batchIndex++)
            {
                var start = seen[jobId];
                _workspace.AppendLog(
                    jobId,
                    lines.Skip(start).Take(batches[batchIndex]).ToArray());
                seen[jobId] += batches[batchIndex];

                var expectedCount = seen[jobId];
                var intermediate = harness.WaitFor(
                    candidate => candidate.Jobs.Count == partitions.Count &&
                                 candidate.Jobs.Single(job => job.JobId == jobId)
                                     .ParsedEventCount == expectedCount,
                    TimeSpan.FromSeconds(20));
                viewModel.ApplySnapshot(intermediate);

                var prefix = fullHistory.Take(expectedCount).ToArray();
                var jobSnapshot = intermediate.Jobs.Single(job => job.JobId == jobId);
                AssertKeys(
                    ReferenceTimelineOracle.Retain(prefix),
                    jobSnapshot.Events,
                    $"{jobId}/{batchIndex}/retained");
                AssertKeys(
                    ReferenceTimelineOracle.Display(prefix),
                    viewModel.Jobs.Single(job => job.JobId == jobId).RecentEvents,
                    $"{jobId}/{batchIndex}/display");
                Assert.AreEqual(
                    prefix.Any(item => item.Timestamp is null),
                    jobSnapshot.HasObservedAmbiguousTimestamps,
                    $"{jobId}/{batchIndex}/ambiguity flag");
            }
        }

        var final = harness.WaitFor(
            candidate => candidate.Jobs.Count == partitions.Count &&
                         partitions.Keys.All(jobId =>
                             candidate.Jobs.Single(job => job.JobId == jobId)
                                 .ParsedEventCount == lines.Length),
            TimeSpan.FromSeconds(20));
        viewModel.ApplySnapshot(final);
        viewModel.ApplySnapshot(final);

        foreach (var jobId in partitions.Keys)
        {
            AssertJobMatchesOracle(final, viewModel, fullHistory, jobId);
        }

        harness.Stop();

        foreach (var jobId in partitions.Keys)
        {
            Assert.AreEqual(lines.Length, CountLoggedJsonlEvents(jobId), jobId);
            Assert.AreEqual(lines.Length, CountLoggedTextEvents(jobId), jobId);
        }
    }

    /// <summary>
    /// A high-timestamp event can remain in the newest-500 retained window long
    /// after its key has left the bounded 10,000-key reader/service dedupe
    /// sets. Replaying the identical line must not duplicate it in the retained
    /// timeline, view model, or persistent monitoring logs.
    /// </summary>
    [TestMethod]
    public void ServiceToViewModel_HighTimestampReplayAfterBoundedDedupe_RemainsSingle()
    {
        const int distinctBeforeReplay = 10_001;
        var jobId = "job_replay_high_timestamp_0001";
        var highTimestamp = BaseTimestamp + 1_000_000;
        var highLine = TestWorkspace.StepStart(
            highTimestamp,
            part: "high-retained",
            message: "high-retained");
        var sentinelTimestamp = BaseTimestamp + 2_000_000;
        var sentinelLine = TestWorkspace.StepStart(
            sentinelTimestamp,
            part: "sentinel-after-replay",
            message: "sentinel-after-replay");

        Assert.IsTrue(ForgeEventParser.TryParse(
            highLine,
            jobId,
            DateTimeOffset.UtcNow,
            out var highParsed));
        Assert.IsTrue(ForgeEventParser.TryParse(
            sentinelLine,
            jobId,
            DateTimeOffset.UtcNow,
            out var sentinelParsed));
        var highKey = highParsed!.EventKey;
        var sentinelKey = sentinelParsed!.EventKey;

        var lines = new string[distinctBeforeReplay];
        lines[0] = highLine;
        for (var index = 1; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                BaseTimestamp + index,
                part: "replay-" + index.ToString("0000"),
                message: "replay-" + index.ToString("0000"));
        }

        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines);

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());

        var beforeReplay = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == distinctBeforeReplay &&
                         candidate.Jobs[0].Events.Count == 500,
            TimeSpan.FromSeconds(30));
        viewModel.ApplySnapshot(beforeReplay);
        viewModel.ApplySnapshot(beforeReplay);

        Assert.AreEqual(
            1,
            beforeReplay.Jobs[0].Events.Count(item => item.EventKey == highKey),
            "high-timestamp event retained once before replay");
        Assert.AreEqual(
            1,
            viewModel.Jobs.Single().RecentEvents.Count(item => item.EventKey == highKey));

        _workspace.AppendLog(jobId, highLine, sentinelLine);
        var afterReplay = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].Events.Any(item => item.EventKey == sentinelKey),
            TimeSpan.FromSeconds(30));
        viewModel.ApplySnapshot(afterReplay);
        viewModel.ApplySnapshot(afterReplay);

        var retained = afterReplay.Jobs[0].Events;
        Assert.AreEqual(500, retained.Count);
        Assert.AreEqual(
            1,
            retained.Count(item => item.EventKey == highKey),
            "replayed high-timestamp event must remain single in the snapshot");
        Assert.AreEqual(
            1,
            viewModel.Jobs.Single().RecentEvents.Count(item => item.EventKey == highKey),
            "replayed high-timestamp event must remain single in the view model");
        Assert.AreEqual(
            retained.Count,
            retained.Select(item => item.EventKey).Distinct(StringComparer.Ordinal).Count(),
            "retained timeline keys must be unique");
        Assert.AreEqual(
            viewModel.Jobs.Single().RecentEvents.Count,
            viewModel.Jobs.Single().RecentEvents
                .Select(item => item.EventKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            "visible timeline keys must be unique");

        harness.Stop();

        Assert.AreEqual(distinctBeforeReplay + 1, CountLoggedJsonlEvents(jobId));
        Assert.AreEqual(distinctBeforeReplay + 1, CountLoggedTextEvents(jobId));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, highTimestamp));
    }

    /// <summary>
    /// Actual service → view-model coverage for 499/500/501/502/800 events and
    /// ascending, descending, out-of-order, equal, all-missing, and first-missing
    /// timestamps, including missing positions 1/499/500/501/502/800.
    /// </summary>
    [TestMethod]
    public void ServiceToViewModel_AllCountsAndTimestampShapes_MatchOracle()
    {
        var expectedFlags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var sources = new Dictionary<string, IReadOnlyList<MonitoringEvent>>(StringComparer.Ordinal);

        foreach (var count in new[] { 499, 500, 501, 502, 800 })
        {
            foreach (var shape in new[] { "ascending", "descending", "outoforder", "equal" })
            {
                AddServiceScenario(
                    $"job_shape_{shape}_{count:0000}",
                    shape,
                    count,
                    0,
                    sources,
                    expectedFlags);
            }

            AddServiceScenario(
                $"job_shape_allmissing_{count:0000}",
                "allmissing",
                count,
                0,
                sources,
                expectedFlags);
        }

        foreach (var missingPosition in new[] { 1, 499, 500, 501, 502, 800 })
        {
            AddServiceScenario(
                $"job_shape_firstmissing_{missingPosition:0000}",
                "firstmissing",
                800,
                missingPosition,
                sources,
                expectedFlags);
        }

        AddServiceScenario(
            "job_shape_multimissing_0800",
            "multimissing",
            800,
            0,
            sources,
            expectedFlags);

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());

        var final = harness.WaitFor(
            candidate => candidate.Jobs.Count == sources.Count &&
                         sources.All(pair =>
                             candidate.Jobs.Single(job => job.JobId == pair.Key)
                                 .ParsedEventCount == pair.Value.Count),
            TimeSpan.FromSeconds(25));
        viewModel.ApplySnapshot(final);
        viewModel.ApplySnapshot(final);

        foreach (var (jobId, source) in sources)
        {
            AssertJobMatchesOracle(final, viewModel, source, jobId);
            var jobSnapshot = final.Jobs.Single(job => job.JobId == jobId);
            Assert.AreEqual(
                expectedFlags[jobId],
                jobSnapshot.HasObservedAmbiguousTimestamps,
                jobId + "/ambiguity flag");
        }

        harness.Stop();

        foreach (var (jobId, source) in sources)
        {
            Assert.AreEqual(source.Count, CountLoggedJsonlEvents(jobId), jobId);
            Assert.AreEqual(source.Count, CountLoggedTextEvents(jobId), jobId);
        }
    }

    /// <summary>
    /// Production bounded retention must match the full-history oracle at every
    /// intermediate prefix and final state for every scenario/partition,
    /// including individual one-event batches.
    /// </summary>
    [TestMethod]
    public void BoundedRetention_AllScenariosAndPartitions_MatchOracleAtEveryPrefix()
    {
        var combinationCount = 0;
        foreach (var scenario in CoreScenarios())
        {
            var full = BuildEvents(scenario.Shape, scenario.Count, scenario.MissingPosition);
            foreach (var (partitionName, partition) in Partitions(full.Length))
            {
                combinationCount++;
                Assert.AreEqual(full.Length, partition.Sum(), scenario.Name);

                var timeline = new TimelineRetentionBuffer(ReferenceTimelineOracle.Capacity);
                var offset = 0;
                for (var batchIndex = 0; batchIndex < partition.Length; batchIndex++)
                {
                    var batchSize = partition[batchIndex];
                    var chunk = full.Skip(offset).Take(batchSize).ToArray();
                    offset += batchSize;
                    timeline.AddRange(chunk);

                    var prefix = full.Take(offset).ToArray();
                    var context = $"{scenario.Name}/{partitionName}/prefix{offset}";
                    AssertKeys(
                        ReferenceTimelineOracle.Retain(prefix),
                        timeline.RetainSourceOrder(),
                        context + "/retained");
                    AssertKeys(
                        ReferenceTimelineOracle.Display(prefix),
                        TimelineOrdering.OrderForDisplay(
                            timeline.RetainSourceOrder(),
                            ReferenceTimelineOracle.Capacity,
                            timeline.HasObservedAmbiguousTimestamps),
                        context + "/display");
                    Assert.AreEqual(
                        prefix.Any(item => item.Timestamp is null),
                        timeline.HasObservedAmbiguousTimestamps,
                        context + "/ambiguity flag");
                    Assert.IsTrue(
                        timeline.BoundedEventCount <= ReferenceTimelineOracle.Capacity * 2,
                        context + "/bounded memory");
                    Assert.AreEqual(
                        timeline.RetainSourceOrder().Count,
                        timeline.RetainSourceOrder()
                            .Select(item => item.EventKey)
                            .Distinct(StringComparer.Ordinal)
                            .Count(),
                        context + "/duplicate free");
                }

                Assert.AreEqual(full.Length, offset, scenario.Name);
            }
        }

        Assert.AreEqual(
            229,
            combinationCount,
            "The documented valid scenario/partition combination count must stay accurate.");
    }

    [TestMethod]
    public void BoundedRetention_MemoryBound_IsTwoWindowsPerJob()
    {
        var allKnown = BuildEvents("outoforder", 800, 0);
        var knownBuffer = new TimelineRetentionBuffer(ReferenceTimelineOracle.Capacity);
        knownBuffer.AddRange(allKnown);

        Assert.AreEqual(1000, knownBuffer.BoundedEventCount);
        Assert.AreEqual(500, knownBuffer.RetainSourceOrder().Count);

        var allMissing = BuildEvents("allmissing", 800, 0);
        var missingBuffer = new TimelineRetentionBuffer(ReferenceTimelineOracle.Capacity);
        missingBuffer.AddRange(allMissing);

        Assert.AreEqual(500, missingBuffer.BoundedEventCount);
        Assert.AreEqual(500, missingBuffer.RetainSourceOrder().Count);
    }

    /// <summary>
    /// Actual incremental tail reader with one appended line per poll, repeated
    /// polls, a duplicate line, cursor advancement, and the same retention
    /// oracle. This covers individual batches without 502 service timers.
    /// </summary>
    [TestMethod]
    public void TailReader_IndividualPollsAndDedupe_MatchOracle()
    {
        var jobId = "job_reader_individual_0001";
        var lines = BuildLines("firstmissing", 502, 502);
        var fullHistory = ParseLines(jobId, lines);
        _workspace.WriteMetadata(jobId);
        var logPath = _workspace.LogPath(jobId);
        File.WriteAllText(logPath, string.Empty);

        var reader = new ForgeLogTailReader(
            jobId,
            logPath,
            _workspace.RunsRoot);
        var timeline = new TimelineRetentionBuffer(ReferenceTimelineOracle.Capacity);

        for (var index = 0; index < lines.Length; index++)
        {
            File.AppendAllText(logPath, lines[index] + "\n");
            var poll = reader.Poll(DateTimeOffset.UtcNow);
            Assert.AreEqual(1, poll.NewEvents.Count, "one appended line");
            timeline.AddRange(poll.NewEvents);
            Assert.AreEqual(
                new FileInfo(logPath).Length,
                reader.Offset,
                "cursor must follow the appended byte length");

            var repeated = reader.Poll(DateTimeOffset.UtcNow);
            Assert.AreEqual(0, repeated.NewEvents.Count, "repeated poll must not emit duplicates");

            if (index == 100)
            {
                File.AppendAllText(logPath, lines[index] + "\n");
                var duplicate = reader.Poll(DateTimeOffset.UtcNow);
                Assert.AreEqual(0, duplicate.NewEvents.Count, "duplicate line must be deduplicated");
                reader.Poll(DateTimeOffset.UtcNow);
            }

            var prefix = fullHistory.Take(index + 1).ToArray();
            AssertKeys(
                ReferenceTimelineOracle.Retain(prefix),
                timeline.RetainSourceOrder(),
                $"reader/prefix{index + 1}/retained");
            AssertKeys(
                ReferenceTimelineOracle.Display(prefix),
                TimelineOrdering.OrderForDisplay(
                    timeline.RetainSourceOrder(),
                    ReferenceTimelineOracle.Capacity,
                    timeline.HasObservedAmbiguousTimestamps),
                $"reader/prefix{index + 1}/display");
            Assert.IsTrue(timeline.BoundedEventCount <= ReferenceTimelineOracle.Capacity * 2);
        }

        Assert.AreEqual(lines.Length, reader.TotalUniqueEvents);
    }

    private void AddServiceScenario(
        string jobId,
        string shape,
        int count,
        int missingPosition,
        IDictionary<string, IReadOnlyList<MonitoringEvent>> sources,
        IDictionary<string, bool> expectedFlags)
    {
        var lines = BuildLines(shape, count, missingPosition);
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines);
        var parsed = ParseLines(jobId, lines);
        Assert.AreEqual(count, parsed.Count, jobId);
        sources[jobId] = parsed;
        expectedFlags[jobId] = parsed.Any(item => item.Timestamp is null);
    }

    private static void AssertJobMatchesOracle(
        MonitoringSnapshot snapshot,
        MainViewModel viewModel,
        IReadOnlyList<MonitoringEvent> fullHistory,
        string jobId)
    {
        var jobSnapshot = snapshot.Jobs.Single(job => job.JobId == jobId);
        AssertKeys(
            ReferenceTimelineOracle.Retain(fullHistory),
            jobSnapshot.Events,
            jobId + "/retained");
        AssertKeys(
            ReferenceTimelineOracle.Display(fullHistory),
            viewModel.Jobs.Single(job => job.JobId == jobId).RecentEvents,
            jobId + "/display");
    }

    private static void AssertKeys(
        IReadOnlyList<MonitoringEvent> expected,
        IReadOnlyList<MonitoringEvent> actual,
        string context)
    {
        Assert.AreEqual(expected.Count, actual.Count, context + " count");
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(
                expected[index].EventKey,
                actual[index].EventKey,
                $"{context} index {index}");
        }
    }

    private MainViewModel CreateViewModel() =>
        new(
            () => throw new InvalidOperationException("Service is started by the harness."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5));

    private ForgeMonitoringService CreateService() =>
        new(
            _workspace.RunsRoot,
            new MonitoringRunLoggerFactory(_workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => DateTimeOffset.UtcNow);

    private static IEnumerable<(
        string Name,
        int Count,
        string Shape,
        int MissingPosition)> CoreScenarios()
    {
        foreach (var count in new[] { 499, 500, 501, 502, 800 })
        {
            foreach (var shape in new[] { "ascending", "descending", "outoforder", "equal" })
            {
                yield return ($"{shape}-{count}", count, shape, 0);
            }

            yield return ($"allmissing-{count}", count, "allmissing", 0);
            yield return ($"multimissing-{count}", count, "multimissing", 0);
        }

        foreach (var position in new[] { 1, 499, 500, 501, 502, 800 })
        {
            yield return ($"firstmissing-800-p{position}", 800, "firstmissing", position);
        }

        yield return ("firstmissing-502-p502", 502, "firstmissing", 502);
    }

    private static IEnumerable<(string Name, int[] Sizes)> Partitions(int total)
    {
        yield return ("single", new[] { total });
        yield return ("individual", Enumerable.Repeat(1, total).ToArray());

        if (total > 500)
        {
            yield return ("500+remainder", new[] { 500, total - 500 });
        }

        if (total > 501)
        {
            yield return ("501+remainder", new[] { 501, total - 501 });
        }

        yield return ("uneven", UnevenPartition(total, 17));
        yield return ("random", RandomPartition(total, total * 31 + 17));
        yield return ("common-prefixes", CommonPrefixPartition(total));
    }

    private static int[] UnevenPartition(int total, int seed)
    {
        var sizes = new[] { 7, 13, 3, 29, 1, 50, 11, 2, 101, 5 };
        var result = new List<int>();
        var remaining = total;
        var index = 0;
        while (remaining > 0)
        {
            var size = Math.Min(sizes[(index + seed) % sizes.Length], remaining);
            result.Add(size);
            remaining -= size;
            index++;
        }

        return result.ToArray();
    }

    private static int[] RandomPartition(int total, int seed)
    {
        var result = new List<int>();
        var remaining = total;
        var state = (uint)seed;
        while (remaining > 0)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var size = 1 + (int)(state % (uint)Math.Min(97, remaining));
            result.Add(size);
            remaining -= size;
        }

        return result.ToArray();
    }

    private static int[] CommonPrefixPartition(int total)
    {
        var result = new List<int>();
        var remaining = total;
        foreach (var desired in new[] { 1, 499, 250, int.MaxValue })
        {
            if (remaining == 0)
            {
                break;
            }

            var size = Math.Min(desired, remaining);
            result.Add(size);
            remaining -= size;
        }

        return result.ToArray();
    }

    private static MonitoringEvent[] BuildEvents(string shape, int count, int missingPosition)
    {
        var events = new MonitoringEvent[count];
        var permutation = DeterministicPermutation(count, count * 37 + 11);
        for (var index = 0; index < count; index++)
        {
            long? timestamp = shape switch
            {
                "ascending" => BaseTimestamp + index,
                "descending" => BaseTimestamp + count - index,
                "outoforder" => BaseTimestamp + permutation[index] + 1,
                "equal" => BaseTimestamp,
                "allmissing" => null,
                "multimissing" => IsMultiMissing(index + 1, count)
                    ? null
                    : BaseTimestamp + count - index,
                "firstmissing" => index == missingPosition - 1
                    ? null
                    : BaseTimestamp + count - index,
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
            };

            events[index] = Event(
                $"event-{shape}-{count}-{index:0000}",
                timestamp);
        }

        return events;
    }

    private static bool IsMultiMissing(int position, int count) =>
        position == 1 ||
        position == Math.Max(1, count / 4) ||
        position == Math.Max(1, count / 2) ||
        position == count;

    private static string[] BuildLines(string shape, int count, int missingPosition)
    {
        var events = BuildEvents(shape, count, missingPosition);
        var lines = new string[count];
        for (var index = 0; index < count; index++)
        {
            lines[index] = events[index].Timestamp is { } timestamp
                ? TestWorkspace.StepStart(
                    timestamp.ToUnixTimeMilliseconds(),
                    part: $"part-{shape}-{index:0000}",
                    message: $"msg-{shape}-{index:0000}")
                : TestWorkspace.StepStartWithoutTimestamp(
                    part: $"part-{shape}-{index:0000}",
                    message: $"msg-{shape}-{index:0000}");
        }

        return lines;
    }

    private static IReadOnlyList<MonitoringEvent> ParseLines(
        string jobId,
        IEnumerable<string> lines)
    {
        var events = new List<MonitoringEvent>();
        foreach (var line in lines)
        {
            Assert.IsTrue(
                ForgeEventParser.TryParse(
                    line,
                    jobId,
                    DateTimeOffset.UtcNow,
                    out var parsed),
                "Fixture line must parse.");
            events.Add(parsed!.Event);
        }

        return events;
    }

    private static MonitoringEvent Event(string key, long? timestamp) =>
        new()
        {
            EventKey = key,
            JobId = "job_oracle_reference",
            SessionId = "ses_oracle_reference",
            Timestamp = timestamp is null
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value),
            ObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(BaseTimestamp),
            Category = MonitoringEventCategory.Step,
            Severity = MonitoringEventSeverity.Info,
            Summary = key
        };

    private static int[] DeterministicPermutation(int count, int seed)
    {
        var values = Enumerable.Range(0, count).ToArray();
        var state = (uint)seed;
        for (var index = count - 1; index > 0; index--)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var swap = (int)(state % (uint)(index + 1));
            (values[index], values[swap]) = (values[swap], values[index]);
        }

        return values;
    }

    private int CountLoggedJsonlEvents(string jobId)
    {
        var jsonlPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.jsonl",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(jsonlPath)
            .Select(line => JsonDocument.Parse(line))
            .Count(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("kind").GetString() == "log_event" &&
                           document.RootElement.GetProperty("jobId").GetString() == jobId;
                }
            });
    }

    private int CountLoggedJsonlEventTimestamp(string jobId, long timestamp)
    {
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var jsonlPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.jsonl",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(jsonlPath)
            .Select(line => JsonDocument.Parse(line))
            .Count(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("kind").GetString() == "log_event" &&
                           document.RootElement.GetProperty("jobId").GetString() == jobId &&
                           document.RootElement.TryGetProperty("eventTs", out var eventTs) &&
                           eventTs.GetDateTimeOffset() == expected;
                }
            });
    }

    private int CountLoggedTextEvents(string jobId)
    {
        var textPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.log",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(textPath)
            .Count(line =>
                line.Contains("[" + jobId + "]", StringComparison.Ordinal) &&
                line.Contains("Session step started", StringComparison.Ordinal));
    }

    private sealed class MonitoringHarness : IDisposable, IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<MonitoringSnapshot> _snapshots =
            Channel.CreateUnbounded<MonitoringSnapshot>();

        public Task? Task { get; private set; }

        public void Start(ForgeMonitoringService service)
        {
            Task = System.Threading.Tasks.Task.Run(async () =>
            {
                await service.RunAsync(async snapshot =>
                {
                    await _snapshots.Writer.WriteAsync(snapshot, _cts.Token);
                }, _cts.Token);
            });
        }

        public MonitoringSnapshot WaitFor(
            Func<MonitoringSnapshot, bool> predicate,
            TimeSpan timeout) =>
            System.Threading.Tasks.Task.Run(() => WaitForAsync(predicate, timeout))
                .GetAwaiter()
                .GetResult();

        public void Stop() => StopAsync().GetAwaiter().GetResult();

        public void Dispose() => Stop();

        public async Task<MonitoringSnapshot> WaitForAsync(
            Func<MonitoringSnapshot, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The expected monitoring snapshot did not arrive.");
                }

                using var timeoutCts = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token,
                    timeoutCts.Token);
                var snapshot = await _snapshots.Reader.ReadAsync(linked.Token);
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
        }

        public async Task StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            if (Task is not null)
            {
                try
                {
                    await Task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during cancellation.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts.Dispose();
            _snapshots.Writer.TryComplete();
        }
    }
}
