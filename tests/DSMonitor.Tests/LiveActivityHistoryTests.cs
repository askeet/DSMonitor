using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class LiveActivityHistoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Started =
        new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private const string SourcePath = @"C:\synthetic\runs\job.log";

    private static readonly LedgerSourceIdentity Identity = new(11, 22, 33);

    [TestMethod]
    public void Reader_HistoryCapturesSourceTimestampToolDurationAndExactIdentity()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "Status: DONE",
                part: "text-1",
                message: "msg-1"),
            TestWorkspace.ToolUse(
                Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
                "shell",
                status: "completed",
                start: Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
                end: Now.AddMinutes(-9).AddSeconds(2).ToUnixTimeMilliseconds(),
                part: "tool-1",
                message: "msg-1"),
            TestWorkspace.StepFinish(
                Now.AddMinutes(-8).ToUnixTimeMilliseconds(),
                cost: 0.01m,
                part: "finish-1",
                message: "msg-1"));

        var prepared = Prepare(workspace, "job");
        var activity = JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now);

        Assert.IsTrue(activity.HasReadWindow);
        Assert.AreEqual(0L, activity.ReadWindowStart);
        Assert.IsTrue(activity.ReadWindowEnd > 0);
        Assert.AreEqual(2, activity.History.Count);

        var newest = activity.History[0];
        Assert.AreEqual("tool-1", newest.PartId);
        Assert.AreEqual("msg-1", newest.MessageId);
        Assert.AreEqual("job", newest.JobId);
        Assert.AreEqual(TimeSpan.FromSeconds(2), newest.ObservedDuration);
        Assert.AreEqual(
            Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
            newest.DurationStartUnixMilliseconds);
        Assert.AreEqual(
            Now.AddMinutes(-9).AddSeconds(2).ToUnixTimeMilliseconds(),
            newest.DurationEndUnixMilliseconds);
        Assert.IsNotNull(newest.SourceTimestamp);
        Assert.AreEqual(prepared.Evidence.VerifiedPath, newest.SourcePath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(newest.RecordDigest));

        var older = activity.History[1];
        Assert.AreEqual("text-1", older.PartId);
        Assert.IsNull(older.ObservedDuration);
        Assert.AreEqual("Status: DONE", older.Text);
        Assert.IsNotNull(older.SourceTimestamp);

        Assert.IsTrue(
            (newest.SourceTimestamp ?? DateTimeOffset.MinValue) >=
            (older.SourceTimestamp ?? DateTimeOffset.MinValue));
    }

    [TestMethod]
    public void Reader_HistoryCapsTextButKeepsTheBoundedFullValueForTooltips()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            TestWorkspace.Text(
                Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                new string('x', JobLiveActivityReader.MaxHistoryTextLength + 1_000),
                part: "long-text",
                message: "msg"));

        var prepared = Prepare(workspace, "job");
        var activity = JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now);

        var entry = activity.History.Single();
        Assert.AreEqual(JobLiveActivityReader.MaxHistoryTextLength, entry.Text.Length);
        Assert.AreEqual(JobLiveActivityReader.MaxExcerptLength, activity.Doing!.Text.Length);

        var row = new LiveActivityHistoryRowViewModel(entry, null, false);
        Assert.AreEqual(entry.Text, row.ReportedStatusText);
        StringAssert.StartsWith(row.ReportedStatusTooltip, entry.Text);
    }

    [TestMethod]
    public void HistoryBuffer_CapsRetainedEntriesToTheNewestBoundedRows()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var entries = Enumerable.Range(0, JobLiveActivityReader.MaxHistoryEntries + 6)
            .Select(index => Entry(
                "part-" + index,
                index * 10,
                Now.AddSeconds(index),
                part: "p" + index,
                digest: "D" + index))
            .ToArray();

        var retained = buffer.Apply(
            Read(entries, 0, entries.Length * 10L),
            "job",
            SourcePath,
            Observation(captured: entries.Length * 10L));

        Assert.AreEqual(JobLiveActivityReader.MaxHistoryEntries, retained.Count);
        Assert.AreEqual("part-69", retained[0].Text);
        Assert.AreEqual("part-6", retained[^1].Text);
    }

    [TestMethod]
    public void HistoryBuffer_AccumulatesDedupesOrdersAndRejectsOlderReread()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var first = Entry("B", 60, Now.AddMinutes(-5), part: "b", digest: "B");
        var older = Entry("A-old", 80, Now.AddMinutes(-4), part: "a", digest: "A1");
        var unknown = Entry("C", 40, null, part: "c", digest: "C");

        var seeded = buffer.Apply(
            Read([older, first, unknown], 0, 100),
            "job",
            SourcePath,
            Observation(captured: 100));

        CollectionAssert.AreEqual(
            new[] { "A-old", "B", "C" },
            seeded.Select(entry => entry.Text).ToArray());

        var newest = Entry("A-new", 140, Now.AddMinutes(-1), part: "a", digest: "A2");
        var equalTimeLower = Entry("D", 120, Now.AddMinutes(-2), part: "d", digest: "D");
        var equalTimeHigher = Entry("E", 130, Now.AddMinutes(-2), part: "e", digest: "E");

        var refreshed = buffer.Apply(
            Read([first, newest, equalTimeLower, equalTimeHigher], 50, 160),
            "job",
            SourcePath,
            Observation(captured: 160));

        CollectionAssert.AreEqual(
            new[] { "A-new", "E", "D", "B", "C" },
            refreshed.Select(entry => entry.Text).ToArray());

        // An older physical record with the same identity must not replace the
        // newer record that is already retained.
        var afterOlderReread = buffer.Apply(
            Read([first, older], 50, 160),
            "job",
            SourcePath,
            Observation(captured: 160));

        var retainedA = afterOlderReread.Single(entry => entry.PartId == "a");
        Assert.AreEqual("A-new", retainedA.Text);
        Assert.AreEqual(140L, retainedA.ByteOffset);
        Assert.AreEqual(5, afterOlderReread.Count);
    }

    [TestMethod]
    public void HistoryBuffer_ClearsWhenContinuityProofIsMissingOrConflicted()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var a = Entry("A", 60, Now.AddMinutes(-2), part: "a", digest: "A");
        var b = Entry("B", 80, Now.AddMinutes(-1), part: "b", digest: "B");
        buffer.Apply(Read([a, b], 0, 100), "job", SourcePath, Observation(captured: 100));

        // No overlapping complete record remains visible.
        var noOverlap = buffer.Apply(
            Read([Entry("C", 150, Now, part: "c", digest: "C")], 150, 160),
            "job",
            SourcePath,
            Observation(captured: 160));
        CollectionAssert.AreEqual(
            new[] { "C" },
            noOverlap.Select(entry => entry.Text).ToArray());

        // A new generation cannot inherit the prior history.
        buffer.Apply(Read([a, b], 0, 100), "job", SourcePath, Observation(captured: 100));
        var newGeneration = buffer.Apply(
            Read([a, b, Entry("D", 110, Now, part: "d", digest: "D")], 0, 120),
            "job",
            SourcePath,
            Observation(generation: 2, captured: 120));
        CollectionAssert.AreEqual(
            new[] { "D", "B", "A" },
            newGeneration.Select(entry => entry.Text).ToArray());

        // A shorter capture is a truncation signal.
        buffer.Apply(Read([a, b], 0, 100), "job", SourcePath, Observation(captured: 100));
        var shortened = buffer.Apply(
            Read([Entry("E", 20, Now, part: "e", digest: "E")], 0, 50),
            "job",
            SourcePath,
            Observation(captured: 50));
        CollectionAssert.AreEqual(
            new[] { "E" },
            shortened.Select(entry => entry.Text).ToArray());

        // A verified path change cannot inherit the prior buffer.
        buffer.Apply(Read([a, b], 0, 100), "job", SourcePath, Observation(captured: 100));
        var otherPath = buffer.Apply(
            Read([a, b], 0, 100),
            "job",
            @"C:\synthetic\runs\replacement.log",
            Observation(captured: 100));
        CollectionAssert.AreEqual(
            new[] { "B", "A" },
            otherPath.Select(entry => entry.Text).ToArray());

        // A missing/unavailable read window clears transient history.
        var cleared = buffer.Apply(
            new LiveActivitySnapshot(),
            "job",
            SourcePath,
            Observation(captured: 100));
        Assert.AreEqual(0, cleared.Count);
        Assert.IsNull(buffer.JobId);
    }

    [TestMethod]
    public void HistoryBuffer_IsStrictlyJobIsolated()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var own = Entry("own", 60, Now.AddMinutes(-1), jobId: "job", part: "p");
        var foreign = Entry("foreign", 70, Now, jobId: "other-job", part: "p2");

        var first = buffer.Apply(
            Read([own, foreign], 0, 100),
            "job",
            SourcePath,
            Observation(captured: 100));
        CollectionAssert.AreEqual(
            new[] { "own" },
            first.Select(entry => entry.Text).ToArray());

        var replacementJob = Entry(
            "replacement",
            80,
            Now,
            jobId: "other-job",
            part: "p");
        var switched = buffer.Apply(
            Read([replacementJob], 50, 100),
            "other-job",
            SourcePath,
            Observation(jobId: "other-job", captured: 100));
        CollectionAssert.AreEqual(
            new[] { "replacement" },
            switched.Select(entry => entry.Text).ToArray());
        Assert.AreEqual("other-job", switched.Single().JobId);
    }

    [TestMethod]
    public void Row_HistoryOrderStatusBindingDurationAndSpinnerAreJobScoped()
    {
        var older = Entry(
            "Status: DONE but older",
            100,
            Now.AddMinutes(-3),
            jobId: "job",
            message: "msg-p1",
            part: "p1",
            digest: "P1");
        var newer = Entry(
            "current text",
            140,
            Now.AddMinutes(-1),
            jobId: "job",
            message: "msg-p2",
            part: "p2",
            digest: "P2",
            duration: TimeSpan.FromSeconds(2));
        var unknown = Entry(
            "unknown text",
            200,
            null,
            jobId: "job",
            message: "msg-p3",
            part: "p3",
            digest: "P3");
        var foreign = Entry(
            "foreign text",
            150,
            Now,
            jobId: "other-job",
            message: "msg-p4",
            part: "p4",
            digest: "P4");

        var report = CompleteReport("job", "ses", "msg-p1", "p1", older.Text, SourcePath, 100);
        var status = new ReportedStatusExtraction(
            ReportedStatusState.Reported,
            "DONE",
            Array.Empty<ReportedStatusDeclaration>(),
            "synthetic complete report",
            report);

        var doing = new ActivityExcerpt(
            "current text",
            "Assistant-reported text",
            "job",
            "ses",
            "msg-p2",
            "p2",
            SourcePath,
            140,
            newer.SourceTimestamp,
            Now)
        {
            RecordDigest = "P2"
        };

        var snapshot = Snapshot(
            ForgeJobStatus.Running,
            [unknown, newer, foreign, older],
            status,
            doing);

        var row = new JobRowViewModel(snapshot);
        CollectionAssert.AreEqual(
            new[] { "p2", "p1", "p3" },
            row.LiveActivityHistory.Select(item => item.Entry.PartId).ToArray());
        Assert.IsTrue(row.HasLiveActivityHistory);

        var newerRow = row.LiveActivityHistory[0];
        var olderRow = row.LiveActivityHistory[1];
        var unknownRow = row.LiveActivityHistory[2];

        Assert.AreEqual("2.0 s", newerRow.DurationText);
        StringAssert.Contains(newerRow.DurationTooltip, "same-part");
        Assert.AreEqual(
            newer.SourceTimestamp!.Value.ToLocalTime().ToString(
                "MMM dd HH:mm:ss",
                CultureInfo.InvariantCulture),
            newerRow.StartedText);
        StringAssert.Contains(newerRow.StartedTooltip, "Source timestamp");

        Assert.AreEqual("N/A", olderRow.DurationText);
        StringAssert.Contains(olderRow.DurationTooltip, "no reliable same-part");
        Assert.AreEqual("—", unknownRow.StartedText);

        // The reported status matches only the exact p1 target.
        Assert.AreEqual("DONE", olderRow.ReportedStatusText);
        StringAssert.Contains(olderRow.ReportedStatusTooltip, "unverified agent-reported");
        Assert.AreEqual("current text", newerRow.ReportedStatusText);
        StringAssert.Contains(newerRow.ReportedStatusTooltip, "not authoritative");
        Assert.AreEqual("unknown text", unknownRow.ReportedStatusText);

        // Only the exact current Doing target is a defensible spinner.
        Assert.IsTrue(newerRow.IsCurrentActiveTarget);
        Assert.IsFalse(olderRow.IsCurrentActiveTarget);
        Assert.IsFalse(unknownRow.IsCurrentActiveTarget);

        var blocked = new JobRowViewModel(snapshot with { Status = ForgeJobStatus.Blocked });
        Assert.IsFalse(blocked.LiveActivityHistory.Any(item => item.IsCurrentActiveTarget));

        var staleDoing = new JobRowViewModel(snapshot with
        {
            LiveActivity = snapshot.LiveActivity with
            {
                Doing = doing with { RecordDigest = "different-digest" }
            }
        });
        Assert.IsFalse(staleDoing.LiveActivityHistory.Any(item => item.IsCurrentActiveTarget));

        // A status bound to another job/path/offset must not leak into a row.
        var foreignStatus = new ReportedStatusExtraction(
            ReportedStatusState.Reported,
            "DONE",
            Array.Empty<ReportedStatusDeclaration>(),
            "synthetic complete report",
            report with { JobId = "other-job" });
        var foreignSnapshot = Snapshot(
            ForgeJobStatus.Running,
            [older],
            foreignStatus,
            null);
        var foreignRow = new JobRowViewModel(foreignSnapshot);
        Assert.AreEqual(older.Text, foreignRow.LiveActivityHistory.Single().ReportedStatusText);
    }

    [TestMethod]
    public async Task Service_HistorySurvivesRefreshDedupesUpdatedPartAndClearsOnDelete()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0", status: "RUNNING");
        workspace.WriteLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "first",
                part: "p1",
                message: "m1"));
        var database = Path.Combine(workspace.Root, "events-v3.db");
        var clockTicks = 0;
        DateTimeOffset Clock() => Now.AddMilliseconds(Interlocked.Increment(ref clockTicks));

        await using var harness = new ServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new MonitoringRunLoggerFactory(workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            Clock,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        var initial = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 1,
            TimeSpan.FromSeconds(10));
        Assert.AreEqual("first", History(initial, "job0").Single().Text);

        workspace.AppendLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
                "second",
                part: "p2",
                message: "m1"));
        var appended = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 2 &&
                        History(snapshot, "job0").Any(entry => entry.PartId == "p2"),
            TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(
            new[] { "second", "first" },
            History(appended, "job0").Select(entry => entry.Text).ToArray());

        workspace.AppendLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "first-updated",
                part: "p1",
                message: "m1"));
        var updated = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 2 &&
                        History(snapshot, "job0").Any(entry =>
                            entry.PartId == "p1" && entry.Text == "first-updated"),
            TimeSpan.FromSeconds(10));
        Assert.AreEqual(
            "first-updated",
            History(updated, "job0").Single(entry => entry.PartId == "p1").Text);

        File.Delete(workspace.LogPath("job0"));
        var deleted = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 0,
            TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, History(deleted, "job0").Count);
    }

    [TestMethod]
    public async Task Service_HistoryClearsOnConflictingImmutableSourceIdentity()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0", status: "RUNNING");
        workspace.WriteLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "first",
                part: "p1",
                message: "m1"));
        var database = Path.Combine(workspace.Root, "events-v3.db");

        await using var harness = new ServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new MonitoringRunLoggerFactory(workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => Now,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 1,
            TimeSpan.FromSeconds(10));

        // A reused exact identity with a changed immutable timestamp is a
        // source-integrity conflict, not a supported history update.
        workspace.AppendLog(
            "job0",
            TestWorkspace.Text(
                Now.AddMinutes(-9).ToUnixTimeMilliseconds(),
                "conflicting-update",
                part: "p1",
                message: "m1"));

        var conflicted = await harness.WaitForAsync(
            snapshot => History(snapshot, "job0").Count == 0 &&
                        snapshot.Jobs.Single(item => item.JobId == "job0").Warnings.Any(
                            warning => warning.Contains(
                                "conflicting",
                                StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));
        var job = conflicted.Jobs.Single(item => item.JobId == "job0");
        Assert.AreEqual(0, job.LiveActivity.History.Count);
    }

    [TestMethod]
    public async Task Service_HistoryKeepsJobsWithSharedSessionAndIdentitiesIsolated()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("jobA", status: "RUNNING");
        workspace.WriteMetadata("jobB", status: "RUNNING");
        workspace.WriteLog(
            "jobA",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "A-only",
                session: "shared-session",
                part: "same-part",
                message: "same-message"));
        workspace.WriteLog(
            "jobB",
            TestWorkspace.Text(
                Now.AddMinutes(-10).ToUnixTimeMilliseconds(),
                "B-only",
                session: "shared-session",
                part: "same-part",
                message: "same-message"));
        var database = Path.Combine(workspace.Root, "events-v3.db");

        await using var harness = new ServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new MonitoringRunLoggerFactory(workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => Now,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        var snapshot = await harness.WaitForAsync(
            value => History(value, "jobA").Count == 1 &&
                     History(value, "jobB").Count == 1,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual("A-only", History(snapshot, "jobA").Single().Text);
        Assert.AreEqual("jobA", History(snapshot, "jobA").Single().JobId);
        Assert.AreEqual("B-only", History(snapshot, "jobB").Single().Text);
        Assert.AreEqual("jobB", History(snapshot, "jobB").Single().JobId);
    }

    [STATestClass]
    public sealed class LayoutTests
    {
        [TestMethod]
        public void SelectingAnotherJobDoesNotMoveOrClearPerJobHistory()
        {
            using var workspace = new TestWorkspace();
            var dataRoot = Path.Combine(workspace.Root, "data");
            var composition = new MonitoringComposition(new MonitoringCompositionOptions(
                workspace.RunsRoot,
                workspace.LogsRoot,
                Path.Combine(dataRoot, "events-v3.db"),
                dataRoot,
                TimeSpan.FromSeconds(1)));
            var window = new MainWindow(composition);
            try
            {
                var viewModel = (MainViewModel)window.DataContext;
                var jobA = SnapshotWithHistory("jobA", "A-only");
                var jobB = SnapshotWithHistory("jobB", "B-only");
                viewModel.ApplySnapshot(new MonitoringSnapshot
                {
                    ObservedAt = Now,
                    State = MonitoringState.Monitoring,
                    Jobs = [jobA, jobB],
                    Warnings = Array.Empty<string>()
                });

                var rowA = viewModel.Jobs.Single(job => job.JobId == "jobA");
                var rowB = viewModel.Jobs.Single(job => job.JobId == "jobB");
                Assert.AreEqual("A-only", rowA.LiveActivityHistory.Single().ReportedStatusText);
                Assert.AreEqual("B-only", rowB.LiveActivityHistory.Single().ReportedStatusText);

                viewModel.SelectedJob = rowA;
                viewModel.SelectedJob = rowB;
                viewModel.SelectedJob = rowA;

                Assert.AreEqual("A-only", rowA.LiveActivityHistory.Single().ReportedStatusText);
                Assert.AreEqual("B-only", rowB.LiveActivityHistory.Single().ReportedStatusText);
            }
            finally
            {
                window.Close();
            }
        }

        [TestMethod]
        public void LiveActivityTabPrecedesOverviewAndUsesExactlyThreeColumns()
        {
            using var workspace = new TestWorkspace();
            var dataRoot = Path.Combine(workspace.Root, "data");
            var composition = new MonitoringComposition(new MonitoringCompositionOptions(
                workspace.RunsRoot,
                workspace.LogsRoot,
                Path.Combine(dataRoot, "events-v3.db"),
                dataRoot,
                TimeSpan.FromSeconds(1)));

            var window = new MainWindow(composition);
            try
            {
                var tabs = window.FindName("SelectedJobTabs") as TabControl;
                Assert.IsNotNull(tabs);
                var headers = tabs.Items
                    .OfType<TabItem>()
                    .Select(tab => tab.Header?.ToString())
                    .ToArray();
                Assert.IsTrue(headers.Length >= 2);
                Assert.AreEqual("Live Activity", headers[0]);
                Assert.AreEqual("Overview", headers[1]);
                CollectionAssert.Contains(headers, "Timeline");
                CollectionAssert.Contains(headers, "Tool Activity");
                CollectionAssert.Contains(headers, "Tests");
                CollectionAssert.Contains(headers, "Files");
                CollectionAssert.Contains(headers, "Raw Log");

                var grid = window.FindName("LiveActivityHistoryGrid") as DataGrid;
                Assert.IsNotNull(grid);
                CollectionAssert.AreEqual(
                    new[] { "Started", "Duration", "Reported Status" },
                    grid.Columns.Select(column => column.Header?.ToString()).ToArray());
                Assert.AreEqual(30d, grid.RowHeight);
                var binding = BindingOperations.GetBinding(
                    grid,
                    ItemsControl.ItemsSourceProperty);
                Assert.IsNotNull(binding);
                Assert.AreEqual("LiveActivityHistory", binding.Path.Path);

                var template = window.FindResource(
                    "LiveActivityHistoryStatusCellTemplate") as DataTemplate;
                Assert.IsNotNull(template);
                var content = (FrameworkElement)template.LoadContent();
                var spinner = FindDescendant<ContentControl>(content);
                Assert.IsNotNull(spinner);
                var spinnerBinding = BindingOperations.GetBinding(
                    spinner,
                    UIElement.VisibilityProperty);
                Assert.IsNotNull(spinnerBinding);
                Assert.AreEqual("IsCurrentActiveTarget", spinnerBinding.Path.Path);

                var statusText = FindDescendant<TextBlock>(content);
                Assert.IsNotNull(statusText);
                var textBinding = BindingOperations.GetBinding(
                    statusText,
                    TextBlock.TextProperty);
                Assert.IsNotNull(textBinding);
                Assert.AreEqual("ReportedStatusText", textBinding.Path.Path);
            }
            finally
            {
                window.Close();
            }
        }

        private static T? FindDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is T match)
                {
                    return match;
                }

                if (child is DependencyObject dependencyObject &&
                    FindDescendant<T>(dependencyObject) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private static IReadOnlyList<LiveActivityHistoryEntry> History(
        MonitoringSnapshot snapshot,
        string jobId) =>
        snapshot.Jobs.Single(job => job.JobId == jobId).LiveActivity.History;

    private static PreparedActivity Prepare(TestWorkspace workspace, string jobId)
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == jobId);
        var database = Path.Combine(workspace.Root, "activity.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var result = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation);
        return new PreparedActivity(job, evidence[jobId], result.SourceObservation!);
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
        TimeSpan? duration = null) =>
        new(
            jobId,
            session,
            message,
            part,
            "Assistant-reported text",
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            digest,
            duration);

    private static LiveActivitySnapshot Read(
        IEnumerable<LiveActivityHistoryEntry> entries,
        long start,
        long end) =>
        new()
        {
            History = entries.ToArray(),
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

    private static ForgeJobSnapshot SnapshotWithHistory(string jobId, string text)
    {
        var entry = Entry(
            text,
            100,
            Now.AddMinutes(-1),
            jobId: jobId,
            session: "shared-session",
            message: "same-message",
            part: "same-part",
            digest: jobId);
        return new ForgeJobSnapshot
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = jobId,
                MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                Started = Started,
                ExplicitStatus = ForgeJobStatus.Running
            },
            Status = ForgeJobStatus.Running,
            ObservedAt = Now,
            LiveActivity = new LiveActivitySnapshot
            {
                History = [entry],
                Availability = "Bounded synthetic activity."
            }
        };
    }

    private static FullReportResult CompleteReport(
        string jobId,
        string session,
        string message,
        string part,
        string text,
        string path,
        long offset) =>
        new(
            FullReportStatus.Complete,
            "Complete synthetic report.",
            text,
            jobId,
            session,
            message,
            part,
            path,
            offset,
            text.Length,
            Now.AddMinutes(-3),
            Now,
            "Assistant-reported text");

    private sealed record PreparedActivity(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);

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
