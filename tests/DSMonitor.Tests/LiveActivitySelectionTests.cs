using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class LiveActivitySelectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Started =
        new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private const string SourcePath = @"C:\synthetic\runs\job.log";

    [TestMethod]
    public void Selection_DefaultsToNewestAndKeepsExactPartAcrossRefreshWithFallback()
    {
        var older = Entry("older", 100, Now.AddMinutes(-3), part: "p1");
        var newer = Entry("newer", 200, Now.AddMinutes(-1), part: "p2");
        var row = new JobRowViewModel(Snapshot([older, newer]));

        Assert.AreEqual("p2", row.SelectedActivity!.Entry.PartId);

        row.SelectedActivity = row.LiveActivityHistory.Single(
            item => item.Entry.PartId == "p1");
        var updatedOlder = Entry("older updated", 120, Now.AddMinutes(-3), part: "p1");
        var newest = Entry("newest", 300, Now, part: "p3");
        row.Update(Snapshot([updatedOlder, newer, newest]), Now);

        Assert.AreEqual("p1", row.SelectedActivity!.Entry.PartId);
        Assert.AreEqual(120L, row.SelectedActivity.Entry.ByteOffset);
        Assert.AreEqual("older updated", row.SelectedActivity.Entry.Text);

        // A DataGrid ItemsSource reset pushes a transient null; it must not
        // erase the job's independent selection.
        row.SelectedActivity = null;
        Assert.AreEqual("p1", row.SelectedActivity!.Entry.PartId);

        row.Update(Snapshot([newer, newest]), Now);
        Assert.AreEqual("p3", row.SelectedActivity!.Entry.PartId);
    }

    [TestMethod]
    public void Selection_IsIndependentPerJob()
    {
        var rowA = new JobRowViewModel(Snapshot(
            [Entry("a-old", 100, Now.AddMinutes(-2), jobId: "jobA", part: "a1"),
             Entry("a-new", 200, Now.AddMinutes(-1), jobId: "jobA", part: "a2")],
            jobId: "jobA"));
        var rowB = new JobRowViewModel(Snapshot(
            [Entry("b-old", 100, Now.AddMinutes(-2), jobId: "jobB", part: "b1"),
             Entry("b-new", 200, Now.AddMinutes(-1), jobId: "jobB", part: "b2")],
            jobId: "jobB"));

        rowA.SelectedActivity = rowA.LiveActivityHistory.Single(
            item => item.Entry.PartId == "a1");
        rowB.SelectedActivity = rowB.LiveActivityHistory.Single(
            item => item.Entry.PartId == "b1");

        Assert.AreEqual("a1", rowA.SelectedActivity!.Entry.PartId);
        Assert.AreEqual("b1", rowB.SelectedActivity!.Entry.PartId);

        rowA.Update(Snapshot(
            [Entry("a-old", 100, Now.AddMinutes(-2), jobId: "jobA", part: "a1"),
             Entry("a-x", 250, Now, jobId: "jobA", part: "a3")],
            jobId: "jobA"), Now);

        Assert.AreEqual("a1", rowA.SelectedActivity!.Entry.PartId);
        Assert.AreEqual("b1", rowB.SelectedActivity!.Entry.PartId);
    }

    [DataTestMethod]
    [DataRow("##Status: DONE", "DONE")]
    [DataRow("## Status: **DONE**", "DONE")]
    [DataRow("##Result:**FAILED** trailing", "FAILED")]
    [DataRow("## Result : **finished.**\nmore", "finished.")]
    [DataRow("##Status:first line\nsecond", "first line")]
    [DataRow("**Status: COMPLETED (implementation).** One UI-correction cycle...",
        "COMPLETED (implementation).")]
    [DataRow("**##Status: DONE**", "DONE")]
    [DataRow("##Status: **unclosed\nrest", "**unclosed")]
    [DataRow("**Status: unclosed", "**Status: unclosed")]
    [DataRow("###Status: DONE", "###Status: DONE")]
    [DataRow(" ##Status: DONE", "DONE")]
    [DataRow("##status: DONE", "DONE")]
    [DataRow("##Statusish: DONE", "##Statusish: DONE")]
    [DataRow("##Status:", null)]
    [DataRow("\nnext line", null)]
    public void StatusLine_ShowsOnlyFirstLineAndNormalizesMarkersAndBold(
        string text,
        string? expected)
    {
        Assert.AreEqual(expected, LiveActivityStatusLinePolicy.Extract(text));
    }

    [TestMethod]
    public void Spinner_ShowsForRunningJobWithoutDoingAndStopsOnTerminalEvidenceOrStop()
    {
        var runningWithoutActivity = Snapshot(
            [],
            status: ForgeJobStatus.Running);
        var row = new JobRowViewModel(runningWithoutActivity);
        Assert.IsTrue(row.IsActive);

        var outOfOrderTerminal = Snapshot(
            [Entry("current", 400, Now, part: "p4")],
            status: ForgeJobStatus.Running,
            doing: Excerpt("current", "p4", 400),
            done: Excerpt("older result", "p2", 100),
            doneEventByteOffset: 500);
        var terminalRow = new JobRowViewModel(outOfOrderTerminal);
        Assert.IsFalse(terminalRow.IsActive);
        Assert.IsFalse(
            terminalRow.LiveActivityHistory.Single().IsCurrentActiveTarget);

        var newerAfterTerminal = Snapshot(
            [Entry("current", 400, Now, part: "p4")],
            status: ForgeJobStatus.Running,
            doing: Excerpt("current", "p4", 400),
            done: Excerpt("older result", "p2", 100),
            doneEventByteOffset: 300);
        var latestActivityRow = new JobRowViewModel(newerAfterTerminal);
        Assert.IsTrue(latestActivityRow.IsActive);
        Assert.IsTrue(
            latestActivityRow.LiveActivityHistory.Single().IsCurrentActiveTarget);

        var newTaskAfterTerminal = Snapshot(
            [],
            status: ForgeJobStatus.Running,
            doneEventByteOffset: 300,
            latestLifecycleByteOffset: 400);
        Assert.IsTrue(new JobRowViewModel(newTaskAfterTerminal).IsActive);

        latestActivityRow.SetMonitoringActive(false);
        Assert.IsFalse(latestActivityRow.IsActive);
        Assert.IsFalse(
            latestActivityRow.LiveActivityHistory.Single().IsCurrentActiveTarget);
    }

    [TestMethod]
    public void Spinner_UsesFreshJobActivityForUnknownStatusAndRejectsStaleOrTerminalActivity()
    {
        var freshActivity = Snapshot(
            [Entry("current", 400, Now, part: "p4")],
            status: ForgeJobStatus.Unknown,
            doing: Excerpt("current", "p4", 400) with
            {
                SourceTimestamp = Now.AddSeconds(-5)
            });
        var row = new JobRowViewModel(freshActivity);
        Assert.IsTrue(row.IsActive);

        var isActiveNotified = false;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(JobRowViewModel.IsActive))
            {
                isActiveNotified = true;
            }
        };
        row.Update(freshActivity with
        {
            ObservedAt = Now.AddSeconds(1),
            LiveActivity = freshActivity.LiveActivity with
            {
                Doing = freshActivity.LiveActivity.Doing! with
                {
                    SourceTimestamp = Now.AddSeconds(-31)
                }
            }
        }, Now.AddSeconds(1));

        Assert.IsTrue(isActiveNotified);
        Assert.IsFalse(row.IsActive, "Historical UNKNOWN activity must not keep the spinner visible.");

        row.Update(freshActivity with
        {
            LiveActivity = freshActivity.LiveActivity with
            {
                DoneEventByteOffset = 500,
                LatestLifecycleByteOffset = 400
            }
        }, Now);
        Assert.IsFalse(row.IsActive, "A delayed progress excerpt must not override later terminal evidence.");

        row.SetMonitoringActive(false);
        Assert.IsFalse(row.IsActive, "Stopping monitoring must hide the spinner.");
    }

    [TestMethod]
    public void Spinner_UsesFreshJobScopedLifecycleHistoryWhenDoingIsUnavailable()
    {
        var row = new JobRowViewModel(Snapshot(
            [Entry("tool activity", 400, Now)],
            status: ForgeJobStatus.Unknown,
            latestLifecycleByteOffset: 400));

        Assert.IsTrue(row.IsActive);
        row.RefreshDuration(Now.AddSeconds(31));
        Assert.IsFalse(row.IsActive, "An old lifecycle offset alone cannot keep UNKNOWN active.");
    }

    [DataTestMethod]
    [DataRow("##Status: **DONE**", "DONE")]
    [DataRow("## Status: PASS", "PASS")]
    [DataRow("##Result: **FAILED** trailing", "FAILED")]
    [DataRow("## Result: finished\nmore", "finished")]
    [DataRow("**Status: COMPLETED (implementation).** One UI-correction cycle...",
        "COMPLETED (implementation).")]
    [DataRow("plain activity text\nmore text", "plain activity text")]
    [DataRow("##Statusish: DONE\nmore", "##Statusish: DONE")]
    public void HistoryStatus_ShowsOnlyTheNormalizedFirstLine(
        string text,
        string expected)
    {
        var row = new LiveActivityHistoryRowViewModel(
            Entry(text, 100, Now.AddMinutes(-1), part: "p"),
            null,
            false);

        Assert.AreEqual(expected, row.ReportedStatusText);
        Assert.AreEqual(expected, row.ReportedStatusTooltip);
    }

    [DataTestMethod]
    [DataRow("## Result: COMPLETED\n### Root cause / decision", "COMPLETED")]
    [DataRow("**Status: COMPLETED (implementation).** Independent native QA...",
        "COMPLETED (implementation).")]
    [DataRow("Done. Only the two permitted files changed; no existing tests or functions were modified.\n\n## Changed paths",
        "Done")]
    [DataRow("##Status: **DONE**", "DONE")]
    [DataRow("## Result : **COMPLETED.**", "COMPLETED.")]
    [DataRow("**##Status: FAILED** trailing", "FAILED")]
    [DataRow("##Status: **UNCLOSED", "**UNCLOSED")]
    [DataRow("\n## Changed paths", "N/A")]
    public void JobAndHistoryStatus_ShareTheSameNormalizedFirstLine(
        string text,
        string expected)
    {
        var entry = Entry(text, 100, Now.AddMinutes(-1), part: "p");
        var extraction = new ReportedStatusExtraction(
            ReportedStatusState.NotReported,
            null,
            Array.Empty<ReportedStatusDeclaration>(),
            "synthetic complete text",
            Complete(text, text.Length));
        var jobRow = new JobRowViewModel(Snapshot([entry], reportedStatus: extraction));
        var historyRow = jobRow.LiveActivityHistory.Single();

        Assert.AreEqual(expected, jobRow.ReportedStatusCellText);
        Assert.AreEqual(expected, historyRow.ReportedStatusText);
        Assert.AreEqual(
            jobRow.ReportedStatusCellTooltip,
            historyRow.ReportedStatusTooltip);
    }

    [TestMethod]
    public void JobAndHistoryStatus_BlankFirstLineShareTheExistingReportedFallback()
    {
        const string text = "\n## Changed paths";
        var entry = Entry(text, 100, Now.AddMinutes(-1), part: "p");
        var extraction = new ReportedStatusExtraction(
            ReportedStatusState.Reported,
            "FALLBACK",
            Array.Empty<ReportedStatusDeclaration>(),
            "synthetic complete text",
            Complete(text, text.Length));
        var jobRow = new JobRowViewModel(Snapshot([entry], reportedStatus: extraction));
        var historyRow = jobRow.LiveActivityHistory.Single();

        Assert.AreEqual("FALLBACK", jobRow.ReportedStatusCellText);
        Assert.AreEqual("FALLBACK", historyRow.ReportedStatusText);
        Assert.AreEqual(
            jobRow.ReportedStatusCellTooltip,
            historyRow.ReportedStatusTooltip);
    }

    [TestMethod]
    public void HistoryStatus_EmptyFirstLineFallsBackToTheExistingReportedValue()
    {
        var entry = Entry(string.Empty, 100, Now.AddMinutes(-1), part: "p");
        var report = Complete("DONE", 4);
        var extraction = new ReportedStatusExtraction(
            ReportedStatusState.Reported,
            "DONE",
            Array.Empty<ReportedStatusDeclaration>(),
            "synthetic complete report",
            report);
        var row = new LiveActivityHistoryRowViewModel(entry, extraction, false);

        Assert.AreEqual("DONE", row.ReportedStatusText);
        Assert.AreEqual("DONE", row.ReportedStatusTooltip);
    }

    [TestMethod]
    public void SelectedActivity_CompleteMultilineTextIsPreservedFromTheFirstCharacter()
    {
        var entry = Entry(
            "## Result: **DONE**\nline one\nline two",
            100,
            Now.AddMinutes(-1),
            part: "p");
        var row = new JobRowViewModel(Snapshot([entry]));
        var key = row.CurrentSelectedActivityLoadKey!.Value;
        var fullText = "## Result: **DONE**\nline one\nline two";
        row.ApplySelectedActivityResult(
            key,
            Complete(fullText, fullText.Length),
            null);

        Assert.AreEqual(fullText, row.SelectedActivityText);
        Assert.IsTrue(row.SelectedActivityTextIsComplete);
        Assert.AreEqual(string.Empty, row.SelectedActivityCompletenessText);
    }

    [TestMethod]
    public void SelectedActivity_ShowsFullTextWithoutTheHistoryPreviewCap()
    {
        var entry = Entry("bounded fallback", 100, Now.AddMinutes(-1), part: "p");
        var row = new JobRowViewModel(Snapshot([entry]));
        var key = row.CurrentSelectedActivityLoadKey!.Value;
        var longText = new string('x', JobLiveActivityReader.MaxHistoryTextLength + 25);
        row.ApplySelectedActivityResult(key, Complete(longText, longText.Length), null);

        Assert.AreEqual(longText, row.SelectedActivityText);
        Assert.IsTrue(row.SelectedActivityTextIsComplete);
        Assert.AreEqual(string.Empty, row.SelectedActivityCompletenessText);
    }

    [TestMethod]
    public void SelectedActivity_IncompleteTextUsesAShortNeutralIndication()
    {
        var entry = Entry("bounded fallback", 100, Now.AddMinutes(-1), part: "p");
        var row = new JobRowViewModel(Snapshot([entry]));
        var key = row.CurrentSelectedActivityLoadKey!.Value;
        var incomplete = new FullReportResult(
            FullReportStatus.Incomplete,
            "N/A — the bounded window clipped the selected record.",
            null,
            "job",
            "ses",
            "msg",
            "p",
            SourcePath,
            100,
            null,
            Now,
            Now,
            "Assistant-reported text");
        row.ApplySelectedActivityResult(key, incomplete, null);

        Assert.AreEqual("bounded fallback", row.SelectedActivityText);
        Assert.IsFalse(row.SelectedActivityTextIsComplete);
        Assert.AreEqual(
            "Incomplete — source text is outside the safely retrieved window.",
            row.SelectedActivityCompletenessText);
    }

    [TestMethod]
    public void SelectedActivity_UsesGuardedFullReportFromTheFirstCharacter()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var original = string.Join(
            "\n",
            Enumerable.Range(0, 40).Select(index => $"line {index} " + new string('v', 20)));
        workspace.WriteLog(
            "job",
            TestWorkspace.Text(
                Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                original,
                part: "p1",
                message: "m1"));

        var prepared = Prepare(workspace, "job");
        var preview = JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now);
        var entry = preview.History.Single();
        var target = new FullReportTarget(
            entry.JobId,
            entry.SessionId,
            entry.MessageId,
            entry.PartId,
            entry.ByteOffset,
            entry.RecordDigest);
        var full = JobLiveActivityReader.ReadFullReport(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            target,
            workspace.RunsRoot,
            Now);
        Assert.AreEqual(FullReportStatus.Complete, full.Status, full.Reason);

        var row = new JobRowViewModel(Snapshot([entry], jobId: "job"));
        var key = row.CurrentSelectedActivityLoadKey!.Value;
        row.ApplySelectedActivityResult(key, full, null);

        Assert.AreEqual(original, row.SelectedActivityText);
        Assert.IsTrue(row.SelectedActivityTextIsComplete);
        Assert.AreEqual(original[0], row.SelectedActivityText[0]);
    }

    [STATestClass]
    public sealed class ViewModelSelectionTests
    {
        [TestMethod]
        public void JobAndActivitySelectionsSurviveRefreshAndRemainIndependent()
        {
            using var workspace = new TestWorkspace();
            using var viewModel = new MainViewModel(
                () => throw new InvalidOperationException("Monitoring is not started."),
                Dispatcher.CurrentDispatcher,
                workspace.RunsRoot,
                TimeSpan.FromSeconds(5));

            viewModel.ApplySnapshot(MonitoringSnapshot(
                jobA: Snapshot(
                    [Entry("a-old", 100, Now.AddMinutes(-2), jobId: "jobA", part: "a1"),
                     Entry("a-new", 200, Now.AddMinutes(-1), jobId: "jobA", part: "a2")],
                    jobId: "jobA"),
                jobB: Snapshot(
                    [Entry("b-old", 100, Now.AddMinutes(-2), jobId: "jobB", part: "b1"),
                     Entry("b-new", 200, Now.AddMinutes(-1), jobId: "jobB", part: "b2")],
                    jobId: "jobB")));

            var jobA = viewModel.Jobs.Single(item => item.JobId == "jobA");
            var jobB = viewModel.Jobs.Single(item => item.JobId == "jobB");
            viewModel.SelectedJob = jobA;
            jobA.SelectedActivity = jobA.LiveActivityHistory.Single(
                item => item.Entry.PartId == "a1");
            viewModel.SelectedJob = jobB;
            jobB.SelectedActivity = jobB.LiveActivityHistory.Single(
                item => item.Entry.PartId == "b1");
            viewModel.SelectedJob = jobA;

            Assert.AreEqual("jobA", viewModel.SelectedJob!.JobId);
            Assert.AreEqual("a1", jobA.SelectedActivity!.Entry.PartId);
            Assert.AreEqual("b1", jobB.SelectedActivity!.Entry.PartId);

            viewModel.ApplySnapshot(MonitoringSnapshot(
                jobA: Snapshot(
                    [Entry("a-old", 120, Now.AddMinutes(-2), jobId: "jobA", part: "a1"),
                     Entry("a-new", 200, Now.AddMinutes(-1), jobId: "jobA", part: "a2")],
                    jobId: "jobA"),
                jobB: Snapshot(
                    [Entry("b-old", 100, Now.AddMinutes(-2), jobId: "jobB", part: "b1"),
                     Entry("b-new", 200, Now.AddMinutes(-1), jobId: "jobB", part: "b2")],
                    jobId: "jobB")));

            Assert.AreEqual("jobA", viewModel.SelectedJob!.JobId);
            Assert.AreEqual("a1", jobA.SelectedActivity!.Entry.PartId);
            Assert.AreEqual(120L, jobA.SelectedActivity.Entry.ByteOffset);
            Assert.AreEqual("b1", jobB.SelectedActivity!.Entry.PartId);
        }

        [STATestClass]
        public sealed class LayoutTests
        {
            [TestMethod]
            public void SelectedActivityTextBoxIsReadOnlyMultilineSelectableAndScrollable()
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
                    var textBox = window.FindName("SelectedActivityTextBox") as TextBox;
                    Assert.IsNotNull(textBox);
                    Assert.IsTrue(textBox.IsReadOnly);
                    Assert.IsTrue(textBox.AcceptsReturn);
                    Assert.AreEqual(TextWrapping.Wrap, textBox.TextWrapping);
                    Assert.AreEqual(
                        ScrollBarVisibility.Auto,
                        textBox.VerticalScrollBarVisibility);
                    var binding = BindingOperations.GetBinding(
                        textBox,
                        TextBox.TextProperty);
                    Assert.IsNotNull(binding);
                    Assert.AreEqual("SelectedActivityText", binding.Path.Path);

                    var grid = window.FindName("LiveActivityHistoryGrid") as DataGrid;
                    Assert.IsNotNull(grid);
                    var selectedBinding = BindingOperations.GetBinding(
                        grid,
                        Selector.SelectedItemProperty);
                    Assert.IsNotNull(selectedBinding);
                    Assert.AreEqual("SelectedActivity", selectedBinding.Path.Path);
                }
                finally
                {
                    window.Close();
                }
            }
        }
    }

    private static LiveActivityHistoryEntry Entry(
        string text,
        long offset,
        DateTimeOffset? timestamp,
        string jobId = "job",
        string part = "part",
        string digest = "digest") =>
        new(
            jobId,
            "ses",
            "msg",
            part,
            "Assistant-reported text",
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            digest,
            null);

    private static ActivityExcerpt Excerpt(
        string text,
        string part,
        long offset) =>
        new(
            text,
            "Assistant-reported text",
            "job",
            "ses",
            "msg",
            part,
            SourcePath,
            offset,
            Now,
            Now)
        {
            RecordDigest = "digest"
        };

    private static ForgeJobSnapshot Snapshot(
        IReadOnlyList<LiveActivityHistoryEntry> history,
        string jobId = "job",
        ForgeJobStatus status = ForgeJobStatus.Running,
        ActivityExcerpt? doing = null,
        ActivityExcerpt? done = null,
        long? doneEventByteOffset = null,
        long? latestLifecycleByteOffset = null,
        ReportedStatusExtraction? reportedStatus = null) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = jobId,
                MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
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
                Done = done,
                DoneEventByteOffset = doneEventByteOffset,
                LatestLifecycleByteOffset = latestLifecycleByteOffset,
                History = history,
                Availability = "Bounded synthetic activity."
            },
            ReportedStatus = reportedStatus
        };

    private static MonitoringSnapshot MonitoringSnapshot(
        ForgeJobSnapshot jobA,
        ForgeJobSnapshot jobB) =>
        new()
        {
            ObservedAt = Now,
            State = MonitoringState.Monitoring,
            ForgeDirectoryAvailable = true,
            Jobs = [jobA, jobB],
            Warnings = Array.Empty<string>()
        };

    private static FullReportResult Complete(string text, long length) =>
        new(
            FullReportStatus.Complete,
            "Complete guarded synthetic capture.",
            text,
            "job",
            "ses",
            "msg",
            "p",
            SourcePath,
            100,
            length,
            Now,
            Now,
            "Assistant-reported text");

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

    private sealed record PreparedActivity(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
