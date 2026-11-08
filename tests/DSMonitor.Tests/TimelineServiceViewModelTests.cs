using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class TimelineServiceViewModelTests
{
    private const long BaseTimestamp = 1_800_000_000_000;

    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void ServiceToViewModel_AllKnownBatches500_501_800_RetainNewestAndLogAll()
    {
        var jobId = "job_order_all_known";
        _workspace.WriteMetadata(jobId);
        var initial = new string[500];
        for (var index = 0; index < initial.Length; index++)
        {
            initial[index] = TestWorkspace.StepStart(
                BaseTimestamp + 500 - index,
                part: "initial-" + index,
                message: "m" + index);
        }

        _workspace.WriteLog(jobId, initial);

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());

        var snapshot500 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 500,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot500);
        var row = viewModel.Jobs.Single();
        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual(BaseTimestamp + 500, row.RecentEvents[0].Timestamp!.Value.ToUnixTimeMilliseconds());

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepStart(
                BaseTimestamp + 1000,
                part: "newest-501",
                message: "m501"));
        var snapshot501 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 501,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot501);
        row = viewModel.Jobs.Single();
        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual(BaseTimestamp + 1000, row.RecentEvents[0].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(BaseTimestamp + 2, row.RecentEvents[^1].Timestamp!.Value.ToUnixTimeMilliseconds());

        var tail = new string[299];
        for (var index = 0; index < tail.Length; index++)
        {
            tail[index] = TestWorkspace.StepStart(
                BaseTimestamp - 1000 - index,
                part: "tail-" + index,
                message: "t" + index);
        }

        _workspace.AppendLog(jobId, tail);
        var snapshot800 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 800,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot800);
        viewModel.ApplySnapshot(snapshot800);

        row = viewModel.Jobs.Single();
        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual(BaseTimestamp + 1000, row.RecentEvents[0].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(BaseTimestamp + 2, row.RecentEvents[^1].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(500, row.RecentEvents.Select(item => item.EventKey).Distinct().Count());
        for (var index = 1; index < row.RecentEvents.Count; index++)
        {
            Assert.IsTrue(row.RecentEvents[index - 1].Timestamp >= row.RecentEvents[index].Timestamp);
        }

        harness.Stop();
        Assert.AreEqual(800, CountLoggedEvents());
        Assert.AreEqual(800, CountLoggedTextEvents(jobId));
    }

    [TestMethod]
    public void ServiceToViewModel_AmbiguousModePersistsAcrossPartitionsAfterBarrierEviction()
    {
        var partitions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["job_part_single_0001"] = 502,
            ["job_part_501_1_0001"] = 501,
            ["job_part_1_501_0001"] = 1,
            ["job_part_250_252_0001"] = 250,
            ["job_part_500_2_0001"] = 500
        };
        var lines = BuildAmbiguous502Lines();
        foreach (var (jobId, initialCount) in partitions)
        {
            _workspace.WriteMetadata(jobId);
            _workspace.WriteLog(jobId, lines.Take(initialCount).ToArray());
        }

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());
        harness.WaitFor(
            candidate => candidate.Jobs.Count == partitions.Count &&
                         partitions.All(pair =>
                             candidate.Jobs.Single(job => job.JobId == pair.Key)
                                 .ParsedEventCount == pair.Value),
            TimeSpan.FromSeconds(15));

        foreach (var (jobId, initialCount) in partitions)
        {
            if (initialCount < lines.Length)
            {
                File.AppendAllText(
                    _workspace.LogPath(jobId),
                    string.Join("\n", lines.Skip(initialCount)) + "\n");
            }
        }

        var final = harness.WaitFor(
            candidate => candidate.Jobs.Count == partitions.Count &&
                         partitions.Keys.All(jobId =>
                             candidate.Jobs.Single(job => job.JobId == jobId)
                                 .ParsedEventCount == 502),
            TimeSpan.FromSeconds(15));
        viewModel.ApplySnapshot(final);
        viewModel.ApplySnapshot(final);

        var expectedTimestamps = Enumerable.Range(1, 500)
            .Select(offset => BaseTimestamp + 501 - offset)
            .ToArray();
        var rows = partitions.Keys
            .Select(jobId => viewModel.Jobs.Single(job => job.JobId == jobId))
            .ToArray();

        foreach (var row in rows)
        {
            var snapshot = final.Jobs.Single(job => job.JobId == row.JobId);
            Assert.IsTrue(snapshot.HasObservedAmbiguousTimestamps);
            Assert.AreEqual(500, row.RecentEvents.Count);
            Assert.IsFalse(row.RecentEvents.Any(item => item.Timestamp is null));
            CollectionAssert.AreEqual(
                expectedTimestamps,
                row.RecentEvents
                    .Select(item => item.Timestamp!.Value.ToUnixTimeMilliseconds())
                    .ToArray(),
                $"Partition {row.JobId} must retain the same mixed-history window.");
        }

        harness.Stop();
        foreach (var jobId in partitions.Keys)
        {
            Assert.AreEqual(502, CountLoggedEvents(jobId));
            Assert.AreEqual(502, CountLoggedTextEvents(jobId));
        }
    }

    [TestMethod]
    public void ServiceToViewModel_OutOfOrderSuccessiveBatches_SortsNewestFirst()
    {
        var jobId = "job_order_out_of_order";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(
            jobId,
            TestWorkspace.StepStart(BaseTimestamp + 200, part: "p200", message: "m200"));

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());
        harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 1,
            TimeSpan.FromSeconds(10));

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepStart(BaseTimestamp + 100, part: "p100", message: "m100"));
        var snapshot2 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 2,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot2);
        CollectionAssert.AreEqual(
            new[] { BaseTimestamp + 200, BaseTimestamp + 100 },
            viewModel.Jobs.Single().RecentEvents
                .Select(item => item.Timestamp!.Value.ToUnixTimeMilliseconds())
                .ToArray());

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepStart(BaseTimestamp + 150, part: "p150", message: "m150"),
            TestWorkspace.StepStart(BaseTimestamp + 300, part: "p300", message: "m300"));
        var snapshot4 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 4,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot4);

        CollectionAssert.AreEqual(
            new[]
            {
                BaseTimestamp + 300,
                BaseTimestamp + 200,
                BaseTimestamp + 150,
                BaseTimestamp + 100
            },
            viewModel.Jobs.Single().RecentEvents
                .Select(item => item.Timestamp!.Value.ToUnixTimeMilliseconds())
                .ToArray());
    }

    [TestMethod]
    public void ServiceToViewModel_MissingFirstKnownSecond_IsSourceOrderBarrier()
    {
        var jobId = "job_order_mixed";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(
            jobId,
            TestWorkspace.StepStartWithoutTimestamp(part: "barrier-0", message: "b0"),
            TestWorkspace.StepStart(BaseTimestamp + 100, part: "known-100", message: "k100"),
            TestWorkspace.StepStart(BaseTimestamp + 200, part: "known-200", message: "k200"));

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());
        var snapshot3 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 3,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot3);
        var row = viewModel.Jobs.Single();
        Assert.AreEqual(3, row.RecentEvents.Count);
        Assert.IsNull(row.RecentEvents[0].Timestamp);
        Assert.AreEqual(BaseTimestamp + 200, row.RecentEvents[1].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(BaseTimestamp + 100, row.RecentEvents[2].Timestamp!.Value.ToUnixTimeMilliseconds());

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepStartWithoutTimestamp(part: "barrier-1", message: "b1"),
            TestWorkspace.StepStart(BaseTimestamp + 50, part: "known-50", message: "k50"));
        var snapshot5 = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 5,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot5);
        viewModel.ApplySnapshot(snapshot5);

        row = viewModel.Jobs.Single();
        Assert.AreEqual(5, row.RecentEvents.Count);
        Assert.AreEqual(5, row.RecentEvents.Select(item => item.EventKey).Distinct().Count());
        Assert.IsNull(row.RecentEvents[0].Timestamp);
        Assert.AreEqual(BaseTimestamp + 200, row.RecentEvents[1].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(BaseTimestamp + 100, row.RecentEvents[2].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.IsNull(row.RecentEvents[3].Timestamp);
        Assert.AreEqual(BaseTimestamp + 50, row.RecentEvents[4].Timestamp!.Value.ToUnixTimeMilliseconds());
    }

    [TestMethod]
    public void ServiceToViewModel_MixedRetentionBoundary_PreservesBarrierContext()
    {
        var jobId = "job_order_mixed_boundary";
        _workspace.WriteMetadata(jobId);
        var lines = new List<string>(800);
        for (var index = 0; index < 400; index++)
        {
            lines.Add(TestWorkspace.StepStart(
                BaseTimestamp + 400 - index,
                part: "before-" + index,
                message: "b" + index));
        }

        lines.Add(TestWorkspace.StepStartWithoutTimestamp(part: "barrier", message: "barrier"));
        for (var index = 0; index < 399; index++)
        {
            lines.Add(TestWorkspace.StepStart(
                BaseTimestamp + 399 - index,
                part: "after-" + index,
                message: "a" + index));
        }

        _workspace.WriteLog(jobId, lines.ToArray());

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());
        var snapshot = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 800 &&
                         candidate.Jobs[0].Events.Count == 500,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot);

        var row = viewModel.Jobs.Single();
        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.AreEqual(BaseTimestamp + 100, row.RecentEvents[0].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.IsNull(row.RecentEvents[100].Timestamp);
        Assert.AreEqual(BaseTimestamp + 399, row.RecentEvents[101].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(BaseTimestamp + 1, row.RecentEvents[^1].Timestamp!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(1, row.RecentEvents.Count(item => item.Timestamp is null));
        Assert.AreEqual(500, row.RecentEvents.Select(item => item.EventKey).Distinct().Count());
    }

    [TestMethod]
    public void ServiceToViewModel_AllMissingRetentionBoundary_IsDeterministic()
    {
        var jobId = "job_order_all_missing";
        _workspace.WriteMetadata(jobId);
        var lines = new string[800];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStartWithoutTimestamp(
                part: "missing-" + index,
                message: "m" + index);
        }

        _workspace.WriteLog(jobId, lines);
        var parsedKeys = lines
            .Select(line =>
            {
                Assert.IsTrue(ForgeEventParser.TryParse(
                    line,
                    jobId,
                    DateTimeOffset.UtcNow,
                    out var parsed));
                return parsed!.EventKey;
            })
            .ToArray();

        using var harness = new MonitoringHarness();
        using var viewModel = CreateViewModel();
        harness.Start(CreateService());
        var snapshot = harness.WaitFor(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 800 &&
                         candidate.Jobs[0].Events.Count == 500,
            TimeSpan.FromSeconds(10));
        viewModel.ApplySnapshot(snapshot);
        viewModel.ApplySnapshot(snapshot);

        var row = viewModel.Jobs.Single();
        Assert.AreEqual(500, row.RecentEvents.Count);
        Assert.IsTrue(row.RecentEvents.All(item => item.Timestamp is null));
        Assert.AreEqual(parsedKeys[300], row.RecentEvents[0].EventKey);
        Assert.AreEqual(parsedKeys[799], row.RecentEvents[^1].EventKey);
        Assert.AreEqual(500, row.RecentEvents.Select(item => item.EventKey).Distinct().Count());
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

    private int CountLoggedEvents(string? jobId = null)
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
                           (jobId is null ||
                            document.RootElement.GetProperty("jobId").GetString() == jobId);
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

    private static string[] BuildAmbiguous502Lines()
    {
        var lines = new string[502];
        lines[0] = TestWorkspace.StepStartWithoutTimestamp(
            part: "ambiguous-0",
            message: "ambiguous-0");
        for (var index = 1; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                BaseTimestamp + 502 - index,
                part: "ambiguous-" + index,
                message: "ambiguous-" + index);
        }

        return lines;
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


