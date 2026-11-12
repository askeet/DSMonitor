using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class LiveActivityStabilizationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Started =
        new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private const string SourcePath = @"C:\synthetic\runs\job.log";
    private static readonly LedgerSourceIdentity Identity = new(11, 22, 33);

    [TestMethod]
    public void HistoryBuffer_ResetsWhenAnyOverlappingPriorOffsetNoLongerMatches()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var a = Entry("A", 20, Now.AddMinutes(-2), part: "a", digest: "A");
        var b = Entry("B", 40, Now.AddMinutes(-1), part: "b", digest: "B");
        buffer.Apply(
            Read([a, b], 0, 100),
            "job",
            SourcePath,
            Observation(captured: 100));

        var c = Entry("C", 20, Now, part: "c", digest: "C");
        var bAgain = Entry("B", 40, Now.AddMinutes(-1), part: "b", digest: "B");
        var rewritten = buffer.Apply(
            Read([c, bAgain], 0, 100),
            "job",
            SourcePath,
            Observation(captured: 100));

        CollectionAssert.AreEqual(
            new[] { "C", "B" },
            rewritten.Select(entry => entry.Text).ToArray());
        Assert.IsFalse(rewritten.Any(entry => entry.PartId == "a"));
    }

    [TestMethod]
    public void HistoryBuffer_DedupedOnlyEvidenceStillRejectsADifferentRecordAtTheSameOffset()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var a = Entry("A", 20, Now.AddMinutes(-2), part: "a", digest: "A");
        var b = Entry("B", 40, Now.AddMinutes(-1), part: "b", digest: "B");
        buffer.Apply(
            Read([a, b], 0, 100, records: Array.Empty<LiveActivityHistoryEntry>()),
            "job",
            SourcePath,
            Observation(captured: 100));

        var c = Entry("C", 20, Now, part: "c", digest: "C");
        var bAgain = Entry("B", 40, Now.AddMinutes(-1), part: "b", digest: "B");
        var rewritten = buffer.Apply(
            Read([c, bAgain], 0, 100, records: Array.Empty<LiveActivityHistoryEntry>()),
            "job",
            SourcePath,
            Observation(captured: 100));

        CollectionAssert.AreEqual(
            new[] { "C", "B" },
            rewritten.Select(entry => entry.Text).ToArray());
    }

    [TestMethod]
    public void HistoryBuffer_CarriesOlderRowsWhenDedupHidesTheOverlapUpdate()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var a = Entry("A", 20, Now.AddMinutes(-3), part: "a", digest: "A");
        var b = Entry("B", 40, Now.AddMinutes(-2), part: "b", digest: "B");
        buffer.Apply(
            Read([a, b], 0, 100),
            "job",
            SourcePath,
            Observation(captured: 100));

        var bAtForty = Entry("B", 40, Now.AddMinutes(-2), part: "b", digest: "B");
        var bUpdated = Entry("B-updated", 65, Now.AddMinutes(-1), part: "b", digest: "B2");
        var refreshed = buffer.Apply(
            Read([bUpdated], 30, 100, records: [bAtForty, bUpdated]),
            "job",
            SourcePath,
            Observation(captured: 100));

        CollectionAssert.AreEqual(
            new[] { "B-updated", "A" },
            refreshed.Select(entry => entry.Text).ToArray());
        Assert.AreEqual(65L, refreshed.Single(entry => entry.PartId == "b").ByteOffset);
    }

    [TestMethod]
    public void Reader_HistoryRecordsPreservesPreDedupEvidenceForTheSamePart()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            TestWorkspace.Text(timestamp, "first", part: "p", message: "m"),
            TestWorkspace.Text(timestamp, "second", part: "p", message: "m"));

        var activity = Read(workspace, "job");

        var displayed = activity.History.Single();
        Assert.AreEqual("second", displayed.Text);
        Assert.AreEqual(2, activity.HistoryRecords.Count);
        Assert.AreEqual(
            2,
            activity.HistoryRecords.Select(entry => entry.ByteOffset).Distinct().Count());
        Assert.IsTrue(activity.HistoryRecords.All(entry => entry.PartId == "p"));
    }

    [TestMethod]
    public void Row_SpinnerStopsOnExplicitSamePartFinishOrObservedToolEnd()
    {
        var textEntry = Entry(
            "finished text",
            100,
            Now.AddMinutes(-1),
            part: "text-part",
            digest: "TEXT");
        var doing = Excerpt(
            "finished text",
            "Assistant-reported text",
            "text-part",
            "TEXT",
            100);
        var running = Snapshot(ForgeJobStatus.Running, [textEntry], null, doing);
        Assert.IsTrue(
            new JobRowViewModel(running).LiveActivityHistory.Single().IsCurrentActiveTarget);

        var finished = running with
        {
            LiveActivity = running.LiveActivity with
            {
                Done = doing with { Kind = "Agent-reported result — unverified" }
            }
        };
        Assert.IsFalse(
            new JobRowViewModel(finished).LiveActivityHistory.Single().IsCurrentActiveTarget);

        var toolEntry = Entry(
            "shell",
            140,
            Now.AddMinutes(-1),
            part: "tool-part",
            digest: "TOOL",
            duration: TimeSpan.FromSeconds(2));
        var toolDoing = Excerpt(
            "shell",
            "Observed tool activity",
            "tool-part",
            "TOOL",
            140);
        var toolSnapshot = Snapshot(ForgeJobStatus.Running, [toolEntry], null, toolDoing);
        Assert.IsFalse(
            new JobRowViewModel(toolSnapshot).LiveActivityHistory.Single().IsCurrentActiveTarget);
    }

    [TestMethod]
    public void Reader_HistorySanitizerHonorsTheCharacterCapForControlCharacters()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            TestWorkspace.Text(
                Now.AddMinutes(-2).ToUnixTimeMilliseconds(),
                new string('x', JobLiveActivityReader.MaxHistoryTextLength) +
                string.Concat(Enumerable.Repeat("\n", 100)),
                part: "long-control",
                message: "msg-control"),
            TestWorkspace.Text(
                Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                new string('y', JobLiveActivityReader.MaxHistoryTextLength - 1) + "\n",
                part: "boundary-control",
                message: "msg-boundary"));

        var activity = Read(workspace, "job");

        var longEntry = activity.History.Single(entry => entry.PartId == "long-control");
        Assert.AreEqual(JobLiveActivityReader.MaxHistoryTextLength, longEntry.Text.Length);
        Assert.AreEqual(
            new string('x', JobLiveActivityReader.MaxHistoryTextLength),
            longEntry.Text);

        var boundaryEntry = activity.History.Single(entry => entry.PartId == "boundary-control");
        Assert.AreEqual(JobLiveActivityReader.MaxHistoryTextLength, boundaryEntry.Text.Length);
        Assert.AreEqual(' ', boundaryEntry.Text[^1]);
    }

    [TestMethod]
    public void Reader_RejectsInvalidOrNegativeToolDurationsButKeepsValidPairs()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var huge = 9_223_372_036_854_775_000L;
        workspace.WriteLog(
            "job",
            TestWorkspace.ToolUse(
                Now.AddMinutes(-4).ToUnixTimeMilliseconds(),
                "valid",
                start: Now.AddMinutes(-4).ToUnixTimeMilliseconds(),
                end: Now.AddMinutes(-4).AddSeconds(2).ToUnixTimeMilliseconds(),
                part: "tool-valid",
                message: "msg-valid"),
            TestWorkspace.ToolUse(
                Now.AddMinutes(-3).ToUnixTimeMilliseconds(),
                "overflow",
                start: huge,
                end: huge + 1,
                part: "tool-overflow",
                message: "msg-overflow"),
            TestWorkspace.ToolUse(
                Now.AddMinutes(-2).ToUnixTimeMilliseconds(),
                "negative",
                start: Now.AddMinutes(-2).AddSeconds(1).ToUnixTimeMilliseconds(),
                end: Now.AddMinutes(-2).ToUnixTimeMilliseconds(),
                part: "tool-negative",
                message: "msg-negative"));

        var activity = Read(workspace, "job");

        var valid = activity.History.Single(entry => entry.PartId == "tool-valid");
        Assert.AreEqual(TimeSpan.FromSeconds(2), valid.ObservedDuration);

        var overflow = activity.History.Single(entry => entry.PartId == "tool-overflow");
        Assert.IsNull(overflow.ObservedDuration);
        Assert.IsNull(overflow.DurationStartUnixMilliseconds);
        Assert.IsNull(overflow.DurationEndUnixMilliseconds);
        Assert.AreEqual(
            "N/A",
            new LiveActivityHistoryRowViewModel(overflow, null, false).DurationText);

        var negative = activity.History.Single(entry => entry.PartId == "tool-negative");
        Assert.IsNull(negative.ObservedDuration);
        Assert.IsNull(negative.DurationStartUnixMilliseconds);
        Assert.IsNull(negative.DurationEndUnixMilliseconds);
    }

    [TestMethod]
    public async Task Service_WholeScanFailurePublishesNeutralActivityWithoutStaleHistory()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0", status: "RUNNING");
        workspace.WriteLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "observed-before-failure",
                part: "p1",
                message: "m1"));
        var database = Path.Combine(workspace.Root, "events-v3.db");

        await using var harness = new ServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new StatusChangeThrowingLoggerFactory(),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => Now,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        var initial = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 1,
            TimeSpan.FromSeconds(10));
        Assert.AreEqual("observed-before-failure", History(initial, "job0").Single().Text);

        workspace.WriteMetadata("job0", status: "DONE");

        var failed = await harness.WaitForAsync(
            snapshot => snapshot.State == MonitoringState.Error,
            TimeSpan.FromSeconds(10));
        var job = failed.Jobs.Single(item => item.JobId == "job0");
        Assert.AreEqual(0, job.LiveActivity.History.Count);
        Assert.IsNull(job.LiveActivity.Doing);
        Assert.IsNull(job.LiveActivity.Done);
        Assert.IsNull(job.ReportedStatus);
        StringAssert.Contains(job.LiveActivity.Availability, "N/A");
    }

    [TestMethod]
    public async Task Service_AggregateReadFailureClearsHistoryBeforeTheJobReturns()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0", status: "RUNNING");
        var aLine = TestWorkspace.Text(
            Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
            "A-old",
            session: "ses",
            part: "a",
            message: "ma");
        var bLine = TestWorkspace.Text(
            Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
            "B-overlap",
            session: "ses",
            part: "b",
            message: "mb");
        var filler = string.Concat(Enumerable.Repeat("padding-line\n", 2_000)).TrimEnd('\n');
        workspace.WriteLog("job0", aLine, filler, bLine);
        var logPath = workspace.LogPath("job0");
        var initialLength = new FileInfo(logPath).Length;
        var database = Path.Combine(workspace.Root, "events-v3.db");

        await using var harness = new ServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new MonitoringRunLoggerFactory(workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => Now,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        var initial = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 2,
            TimeSpan.FromSeconds(10));
        var a = History(initial, "job0").Single(entry => entry.PartId == "a");
        var b = History(initial, "job0").Single(entry => entry.PartId == "b");
        Assert.IsTrue(a.ByteOffset < b.ByteOffset);

        // Corrupt only a value the next successful aggregate read must parse.
        // A later applied event repairs it because its timestamp sorts after
        // the corrupt marker, letting the same worker path recover.
        SetLastActivity(database, "!!!! invalid timestamp");
        await harness.WaitForAsync(
            snapshot => !snapshot.Jobs.Any(item => item.JobId == "job0"),
            TimeSpan.FromSeconds(10));

        // Append enough bytes that the next 64 KB window starts inside the
        // already-read filler before B. A is then outside the window; only a
        // buffer that failed to clear on the aggregate error can still carry it.
        var targetStart = a.ByteOffset + (b.ByteOffset - a.ByteOffset) / 2;
        var cLine = TestWorkspace.Text(
            Now.ToUnixTimeMilliseconds(),
            "C-new",
            session: "ses",
            part: "c",
            message: "mc");
        var cBytes = Encoding.UTF8.GetByteCount(cLine) + 1;
        var required = JobLiveActivityReader.MaxReadBytes + targetStart - initialLength;
        var paddingLength = required - cBytes;
        Assert.IsTrue(paddingLength > 1);
        var padding = new string('p', checked((int)paddingLength - 1)) + "\n";
        File.AppendAllText(logPath, padding + cLine + "\n", Encoding.UTF8);

        var recovered = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Any(entry => entry.PartId == "c"),
            TimeSpan.FromSeconds(10));
        var recoveredHistory = History(recovered, "job0");
        Assert.IsTrue(recoveredHistory.Any(entry => entry.PartId == "b"));
        Assert.IsFalse(recoveredHistory.Any(entry => entry.PartId == "a"));
    }

    [STATestClass]
    public sealed class LifecycleTests
    {
        [TestMethod]
        public async Task StopAsync_SuppressesTheActiveTargetSpinnerWithoutAnotherSnapshot()
        {
            using var workspace = new TestWorkspace();
            var entry = Entry(
                "current text",
                100,
                Now.AddMinutes(-1),
                part: "p",
                digest: "D");
            var doing = Excerpt("current text", "Assistant-reported text", "p", "D", 100);
            var snapshot = Snapshot(ForgeJobStatus.Running, [entry], null, doing);
            using var viewModel = new MainViewModel(
                () => throw new InvalidOperationException("Monitoring is not started."),
                Dispatcher.CurrentDispatcher,
                workspace.RunsRoot,
                TimeSpan.FromSeconds(5));

            viewModel.ApplySnapshot(new MonitoringSnapshot
            {
                ObservedAt = Now,
                State = MonitoringState.Monitoring,
                ForgeDirectoryAvailable = true,
                Jobs = [snapshot]
            });

            var row = viewModel.Jobs.Single();
            Assert.IsTrue(row.LiveActivityHistory.Single().IsCurrentActiveTarget);
            var changed = new List<string?>();
            row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            await viewModel.StopAsync();

            Assert.AreEqual(MonitoringState.Stopped, viewModel.MonitoringState);
            Assert.IsFalse(row.LiveActivityHistory.Single().IsCurrentActiveTarget);
            CollectionAssert.Contains(changed, nameof(JobRowViewModel.LiveActivityHistory));
        }
    }

    private static LiveActivityHistoryEntry Entry(
        string text,
        long offset,
        DateTimeOffset? timestamp,
        string jobId = "job",
        string session = "ses",
        string message = "msg",
        string part = "part",
        string digest = "digest",
        string kind = "Assistant-reported text",
        TimeSpan? duration = null)
    {
        var entry = new LiveActivityHistoryEntry(
            jobId,
            session,
            message,
            part,
            kind,
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            digest,
            duration);
        if (duration is { } observed)
        {
            var start = Now.AddMinutes(-1).ToUnixTimeMilliseconds();
            entry = entry with
            {
                DurationStartUnixMilliseconds = start,
                DurationEndUnixMilliseconds = start + (long)observed.TotalMilliseconds
            };
        }

        return entry;
    }

    private static ActivityExcerpt Excerpt(
        string text,
        string kind,
        string part,
        string digest,
        long offset) =>
        new(
            text,
            kind,
            "job",
            "ses",
            "msg",
            part,
            SourcePath,
            offset,
            Now.AddMinutes(-1),
            Now)
        {
            RecordDigest = digest
        };

    private static LiveActivitySnapshot Read(
        IEnumerable<LiveActivityHistoryEntry> entries,
        long start,
        long end,
        IEnumerable<LiveActivityHistoryEntry>? records = null) =>
        new()
        {
            History = entries.ToArray(),
            HistoryRecords = (records ?? entries).ToArray(),
            ReadWindowStart = start,
            ReadWindowEnd = end
        };

    private static LedgerSourceObservation Observation(
        string jobId = "job",
        long generation = 1,
        long captured = 100) =>
        new(
            jobId,
            SourcePath,
            Identity,
            Identity,
            generation,
            captured,
            captured,
            false);

    private static ForgeJobSnapshot Snapshot(
        ForgeJobStatus status,
        IReadOnlyList<LiveActivityHistoryEntry> history,
        ReportedStatusExtraction? reportedStatus,
        ActivityExcerpt? doing) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = "job",
                MetadataPath = @"C:\synthetic\job.json",
                LogPath = SourcePath,
                HasExplicitLogPath = true,
                Started = Started,
                ExplicitStatus = status
            },
            Status = status,
            LastActivity = Started.AddMinutes(5),
            ObservedAt = Now,
            LiveActivity = new LiveActivitySnapshot
            {
                Doing = doing,
                History = history,
                Availability = "Bounded synthetic activity."
            },
            ReportedStatus = reportedStatus
        };

    private static LiveActivitySnapshot Read(TestWorkspace workspace, string jobId)
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == jobId);
        var database = Path.Combine(workspace.Root, "activity.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var result = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation);
        return JobLiveActivityReader.Read(
            job,
            evidence[jobId],
            result.SourceObservation!,
            workspace.RunsRoot,
            Now);
    }

    private static IReadOnlyList<LiveActivityHistoryEntry> History(
        MonitoringSnapshot snapshot,
        string jobId) =>
        snapshot.Jobs.Single(job => job.JobId == jobId).LiveActivity.History;

    private static void SetLastActivity(string databasePath, string value)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE job_aggregates SET last_activity_utc = $value WHERE job_id = 'job0';";
        command.Parameters.AddWithValue("$value", value);
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    private sealed class StatusChangeThrowingLoggerFactory : IMonitoringLoggerFactory
    {
        public IMonitoringLogger Create(DateTimeOffset startedAt) =>
            new StatusChangeThrowingLogger();
    }

    private sealed class StatusChangeThrowingLogger : IMonitoringLogger
    {
        public string? LogFilePath => null;
        public string? JsonlFilePath => null;
        public string? LastError => null;

        public void RunStarted(DateTimeOffset startedAt) { }

        public void JobDiscovered(ForgeJobMetadata job) { }

        public void JobStatusChanged(
            string jobId,
            ForgeJobStatus previous,
            ForgeJobStatus current) =>
            throw new InvalidOperationException("Synthetic status-change logging failure.");

        public void SessionObserved(string jobId, string sessionId) { }

        public void EventsObserved(IReadOnlyList<MonitoringEvent> events) { }

        public void Warning(string summary) { }

        public void RunStopped(DateTimeOffset stoppedAt) { }

        public void Dispose() { }
    }

    private sealed class ServiceHarness : IAsyncDisposable
    {
        private readonly Channel<MonitoringSnapshot> _snapshots =
            Channel.CreateUnbounded<MonitoringSnapshot>();
        private readonly CancellationTokenSource _cts = new();
        private Task? _task;

        public void Start(ForgeMonitoringService service)
        {
            _task = Task.Run(async () =>
            {
                try
                {
                    await service.RunAsync(
                        snapshot => _snapshots.Writer
                            .WriteAsync(snapshot, _cts.Token)
                            .AsTask(),
                        _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected during test shutdown.
                }
                finally
                {
                    _snapshots.Writer.TryComplete();
                }
            });
        }

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
                    throw new TimeoutException(
                        "The expected monitoring snapshot did not arrive.");
                }

                using var timeoutCts = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token,
                    timeoutCts.Token);
                MonitoringSnapshot snapshot;
                try
                {
                    snapshot = await _snapshots.Reader.ReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The expected monitoring snapshot did not arrive.");
                }

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

            if (_task is not null)
            {
                try
                {
                    await _task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during test shutdown.
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
