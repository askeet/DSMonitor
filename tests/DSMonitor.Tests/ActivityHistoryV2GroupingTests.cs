using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DSMonitor;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Synthetic V2 Activity History grouping, text-part timing, Other isolation,
/// selection and expanded-state regressions. No real Forge or OpenCode data is
/// read; every source record and snapshot is constructed in the test.
/// </summary>
[TestClass]
public sealed class ActivityHistoryV2GroupingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = Now.AddMinutes(-10);
    private static readonly string SourcePath = @"C:\synthetic\v2-job.log";

    [TestMethod]
    public void Grouping_UsesFirstTextUnderPolicyAndKeepsEveryTextPartAsAChild()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-4), "one", textStart: Now.AddMinutes(-4), textEnd: Now.AddMinutes(-3)),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-3), "two", textStart: Now.AddMinutes(-3), textEnd: Now.AddMinutes(-2)),
            Text("job", "msg1", "p3", 30, Now.AddMinutes(-2), "three", textStart: Now.AddMinutes(-2), textEnd: Now.AddMinutes(-1)),
            Tool("job", "msg1", "tool1", 40, Now.AddMinutes(-1), toolStart: Now.AddMinutes(-1), duration: TimeSpan.FromSeconds(30)),
            Step("job", "msg1", "s1", 50, Now.AddMinutes(-1)),
            Step("job", "msg1", "f1", 60, Now, reason: "stop"),
            Text("job", "msg2", "p4", 70, Now, "four", textStart: Now, textEnd: Now.AddSeconds(5))
        };

        var row = new JobRowViewModel(Snapshot("job", entries));

        // Policy order is newest first: msg2/p4, msg1/p3, msg1/p2, msg1/p1, tool, step.
        var groups = row.ActivityHistoryMessageGroups;
        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual("msg2", groups[0].Message.Entry.MessageId);
        Assert.AreEqual("msg1", groups[1].Message.Entry.MessageId);
        Assert.AreEqual("p3", groups[1].Message.Entry.PartId,
            "The first text under the existing history policy is the parent.");

        // Every retained text part is a child, including the representative.
        CollectionAssert.AreEqual(
            new[] { "p1", "p2", "p3" },
            groups[1].Children.Where(child => child.IsTextActivity)
                .Select(child => child.Entry.PartId).ToArray());
        Assert.AreEqual(5, groups[1].Children.Count);
        Assert.AreEqual(1, groups[0].Children.Count);
        Assert.AreEqual(0, row.OtherActivityRows.Count);
        Assert.AreEqual(6, row.LiveActivityHistory.Count);

        // The parent keeps its existing event timing; only the child copy uses
        // the same-record text part interval.
        var parent = groups[1].Message;
        var child = groups[1].Children.Single(item => item.Entry.PartId == "p3");
        Assert.IsFalse(parent.IsTextChild);
        Assert.IsTrue(child.IsTextChild);
        Assert.AreEqual(Now.AddMinutes(-2), parent.StartedSortTimestamp);
        Assert.AreEqual(Now.AddMinutes(-2), child.StartedSortTimestamp);
        Assert.AreEqual("N/A", parent.DurationText);
        Assert.AreEqual("1m 0.000s", child.DurationText);
        Assert.AreEqual(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(child.DurationSortSeconds));

        // Children are chronological, unknown last; parent ordering is unchanged.
        CollectionAssert.AreEqual(
            new[] { "p1", "p2", "p3", "tool1", "s1" },
            groups[1].Children.Select(item => item.Entry.PartId).ToArray());
    }

    [TestMethod]
    public void Grouping_OrphanHistoryRowsBecomeExactOtherAndAreNeverDuplicated()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-3), "text"),
            Tool("job", "msg1", "tool1", 20, Now.AddMinutes(-2), toolStart: Now.AddMinutes(-2), duration: TimeSpan.FromSeconds(2), status: "error", outcome: LiveActivityToolOutcome.Failed),
            Tool("job", "msg2", "tool2", 30, Now.AddMinutes(-1), toolStart: Now.AddMinutes(-1), duration: TimeSpan.FromSeconds(3)),
            Step("job", "msg2", "s1", 40, Now),
            Step("job", "msg2", "f1", 50, Now.AddSeconds(1), reason: "stop")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();

        Assert.AreEqual(1, job.ActivityHistoryMessageGroups.Count);
        Assert.AreEqual("p1", job.ActivityHistoryMessageGroups[0].Message.Entry.PartId);
        CollectionAssert.AreEqual(
            new[] { "p1", "tool1" },
            job.ActivityHistoryMessageGroups[0].Children.Select(item => item.Entry.PartId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "tool2", "s1" },
            job.OtherActivityRows.Select(item => item.Entry.PartId).ToArray());

        Assert.AreEqual("Text Part (1)", viewModel.ActivityTextFilterLabel);
        Assert.AreEqual("Tools (✓0 · ✕1)", viewModel.ActivityToolsFilterLabel);
        Assert.AreEqual("Steps (0)", viewModel.ActivityStepsFilterLabel);
        Assert.AreEqual("Other (2)", viewModel.ActivityOtherFilterLabel);

        // Default: the text parent only. Other is independent and adds exactly
        // the two orphan rows; the grouped child tool is not duplicated there.
        Assert.AreEqual(1, viewModel.FilteredActivityHistory.Count);
        viewModel.ShowActivityOtherFilter = true;
        var visible = viewModel.FilteredActivityHistory;
        Assert.AreEqual(3, visible.Count);
        Assert.AreEqual(3, visible.Select(item => item.Entry.PartId).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void Grouping_ChildrenWithUnknownStartedSortLast()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p-known", 10, Now.AddMinutes(-1), "known"),
            Text("job", "msg1", "p-unknown", 20, null, "unknown")
        };

        var row = new JobRowViewModel(Snapshot("job", entries));
        var group = row.ActivityHistoryMessageGroups.Single();
        Assert.AreEqual("p-known", group.Message.Entry.PartId);
        CollectionAssert.AreEqual(
            new[] { "p-known", "p-unknown" },
            group.Children.Select(child => child.Entry.PartId).ToArray());
        Assert.IsNull(group.Children[1].StartedSortTimestamp);
        Assert.AreEqual("N/A", group.Children[1].StartedText);

        var display = ActivityHistoryDisplayRowViewModel.Project(
            [group], Array.Empty<LiveActivityHistoryRowViewModel>(),
            showText: true, showTools: true, showSteps: true, showOther: false);
        var parent = display.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        var refreshed = ActivityHistoryDisplayRowViewModel.Project(
            [group], Array.Empty<LiveActivityHistoryRowViewModel>(),
            showText: true, showTools: true, showSteps: true, showOther: false);
        CollectionAssert.AreEqual(
            new[] { "p-known", "p-unknown" },
            refreshed.Where(item => item.IsChild)
                .Select(item => item.Activity.Entry.PartId).ToArray());
    }

    [TestMethod]
    public void Timing_TextChildUsesPartIntervalThenMessageThenEventTime()
    {
        var created = Now.AddMinutes(-5);
        var completed = Now.AddMinutes(-1);
        var messageDuration = new OpenCodeMessageDuration(
            "msg1",
            created,
            completed,
            completed - created);
        var partStart = Now.AddMinutes(-4);
        var partEnd = Now.AddMinutes(-3);

        var intervalEntry = Text(
            "job", "msg1", "p1", 10, Now.AddMinutes(-2), "text",
            textStart: partStart,
            textEnd: partEnd);

        var parent = new LiveActivityHistoryRowViewModel(
            intervalEntry, null, false, messageDuration: messageDuration);
        var child = new LiveActivityHistoryRowViewModel(
            intervalEntry, null, false, messageDuration: messageDuration, isGroupedChild: true);

        // Parent keeps the existing message-scoped timing.
        Assert.AreEqual(created, parent.StartedSortTimestamp);
        Assert.AreEqual((completed - created).TotalSeconds, parent.DurationSortSeconds, 0.001);
        // Child prefers its validated same-record part interval.
        Assert.AreEqual(partStart, child.StartedSortTimestamp);
        Assert.AreEqual((partEnd - partStart).TotalSeconds, child.DurationSortSeconds, 0.001);
        StringAssert.Contains(parent.DurationTooltip, "OpenCode assistant message duration");
        StringAssert.Contains(child.DurationTooltip, "Text part interval");
        StringAssert.Contains(child.StartedTooltip, "root source timestamp");

        // No part interval: the message timing is the fallback, then event time.
        var noInterval = intervalEntry with
        {
            TextStartUnixMilliseconds = null,
            TextEndUnixMilliseconds = null
        };
        var messageFallback = new LiveActivityHistoryRowViewModel(
            noInterval, null, false, messageDuration: messageDuration, isGroupedChild: true);
        Assert.AreEqual(created, messageFallback.StartedSortTimestamp);
        Assert.AreEqual((completed - created).TotalSeconds, messageFallback.DurationSortSeconds, 0.001);

        var eventFallback = new LiveActivityHistoryRowViewModel(
            noInterval, null, false, isGroupedChild: true);
        Assert.AreEqual(noInterval.SourceTimestamp, eventFallback.StartedSortTimestamp);
        Assert.IsTrue(double.IsPositiveInfinity(eventFallback.DurationSortSeconds));
        Assert.AreEqual("N/A", eventFallback.DurationText);

        // An invalid pair is never partially used.
        var invalid = intervalEntry with
        {
            TextStartUnixMilliseconds = 5_000,
            TextEndUnixMilliseconds = 4_000
        };
        var invalidChild = new LiveActivityHistoryRowViewModel(
            invalid, null, false, messageDuration: messageDuration, isGroupedChild: true);
        Assert.AreEqual(created, invalidChild.StartedSortTimestamp);
        Assert.AreEqual((completed - created).TotalSeconds, invalidChild.DurationSortSeconds, 0.001);
    }

    [TestMethod]
    public void ExpandedMessage_ShowsFinishedTimeAndNegativeDurationWithoutChangingGroupOrderOrChildren()
    {
        var firstStart = Now.AddMinutes(-5);
        var firstEnd = Now.AddMinutes(-1);
        var secondStart = Now.AddMinutes(-4);
        var secondEnd = Now.AddMinutes(-2);
        LiveActivityHistoryRowViewModel Parent(string id, string part, DateTimeOffset start, DateTimeOffset end) =>
            new(Text("job", id, part, id == "m1" ? 10 : 20, start, id), null, false,
                messageDuration: new OpenCodeMessageDuration(id, start, end, end - start));
        var firstParent = Parent("m1", "p1", firstStart, firstEnd);
        var secondParent = Parent("m2", "p2", secondStart, secondEnd);
        var childEntry = Text("job", "m1", "p1", 10, firstStart, "child",
            textStart: firstStart.AddSeconds(1), textEnd: firstStart.AddSeconds(2));
        var child = new LiveActivityHistoryRowViewModel(childEntry, null, false,
            messageDuration: firstParent.MessageDuration, isGroupedChild: true);
        var groups = new[]
        {
            new ActivityHistoryMessageGroup(firstParent, [child]),
            new ActivityHistoryMessageGroup(secondParent)
        };
        var rows = ActivityHistoryDisplayRowViewModel.Project(groups, [], true, true, true, false);
        var first = rows.Single(row => row.IsMessage && row.Activity.Entry.MessageId == "m1");
        var second = rows.Single(row => row.IsMessage && row.Activity.Entry.MessageId == "m2");
        var originalChildStarted = child.StartedText;
        var originalChildDuration = child.DurationText;

        Assert.AreEqual(StartedDisplayFormatter.Format(firstStart), first.StartedText);
        Assert.AreEqual("4.000s", first.DurationText);
        first.IsExpanded = true;
        Assert.AreEqual(StartedDisplayFormatter.Format(firstEnd), first.StartedText);
        Assert.AreEqual("-4.000s", first.DurationText);
        Assert.AreEqual(StartedDisplayFormatter.Format(secondStart), second.StartedText);
        Assert.AreEqual("2.000s", second.DurationText);
        Assert.AreEqual(originalChildStarted, child.StartedText);
        Assert.AreEqual(originalChildDuration, child.DurationText);
        Assert.AreEqual("m1", rows.Where(row => row.IsMessage).First().Activity.Entry.MessageId);

        first.IsExpanded = false;
        Assert.AreEqual(StartedDisplayFormatter.Format(firstStart), first.StartedText);
        Assert.AreEqual("4.000s", first.DurationText);
        Assert.AreEqual("m1", rows.Where(row => row.IsMessage).First().Activity.Entry.MessageId);
    }

    [TestMethod]
    public void ExpandedMessage_WithoutValidCompletionKeepsExistingTiming()
    {
        var start = Now.AddMinutes(-1);
        var row = new LiveActivityHistoryRowViewModel(
            Text("job", "m1", "p1", 10, start, "text"), null, false);
        var groupRow = ActivityHistoryDisplayRowViewModel.Message(new ActivityHistoryMessageGroup(row));
        var started = groupRow.StartedText;
        var duration = groupRow.DurationText;
        // Exercise expansion with an eligible child while the message interval is unavailable.
        var child = new LiveActivityHistoryRowViewModel(
            Text("job", "m1", "p2", 20, start.AddSeconds(1), "child"), null, false, isGroupedChild: true);
        groupRow = ActivityHistoryDisplayRowViewModel.Message(new ActivityHistoryMessageGroup(row, [child]));
        groupRow.IsExpanded = true;
        Assert.AreEqual(started, groupRow.StartedText);
        Assert.AreEqual(duration, groupRow.DurationText);
        Assert.IsTrue(double.IsPositiveInfinity(groupRow.DurationSortSeconds));
    }

    [TestMethod]
    public void ExpandedMessage_AlwaysEndsWithUnselectableStartMarkerIncludingEmptyAndFilteredGroups()
    {
        var emptyParent = new LiveActivityHistoryRowViewModel(
            Text("job", "empty", "representative", 10, Now.AddMinutes(-3), "parent"), null, false);
        var emptyGroup = new ActivityHistoryMessageGroup(emptyParent);
        var emptyDisplay = emptyGroup.DisplayRow;
        Assert.IsTrue(emptyDisplay.HasChildren);
        Assert.IsTrue(emptyDisplay.HasVisibleChildren);
        emptyDisplay.IsExpanded = true;
        var emptyRows = ActivityHistoryDisplayRowViewModel.Project(
            [emptyGroup], [], false, false, false, false);
        Assert.AreEqual(2, emptyRows.Count);
        Assert.IsTrue(emptyRows[1].IsStartMarker);
        Assert.AreEqual("Start of message", emptyRows[1].ReportedStatusText);
        Assert.AreEqual(string.Empty, emptyRows[1].DurationText);
        Assert.AreEqual(string.Empty, emptyRows[1].StartedText);
        Assert.IsFalse(emptyRows[1].IsMessage);
        Assert.IsTrue(emptyRows[1].IsChild);

        var activity = new LiveActivityHistoryRowViewModel(
            Text("job", "normal", "p1", 20, Now.AddMinutes(-2), "child"), null, false);
        var normalGroup = new ActivityHistoryMessageGroup(
            new LiveActivityHistoryRowViewModel(Text("job", "normal", "p1", 20, Now.AddMinutes(-2), "parent"), null, false),
            [activity]);
        normalGroup.DisplayRow.IsExpanded = true;
        var filteredRows = ActivityHistoryDisplayRowViewModel.Project(
            [normalGroup], [], false, false, false, false);
        Assert.AreEqual(2, filteredRows.Count);
        Assert.IsTrue(filteredRows[^1].IsStartMarker);

        normalGroup.DisplayRow.IsExpanded = false;
        var collapsed = ActivityHistoryDisplayRowViewModel.Project(
            [normalGroup], [], true, true, true, false);
        Assert.AreEqual(1, collapsed.Count);
        normalGroup.DisplayRow.IsExpanded = true;
        var expanded = ActivityHistoryDisplayRowViewModel.Project(
            [normalGroup], [], true, true, true, false, ListSortDirection.Descending);
        Assert.AreEqual("p1", expanded[1].Activity.Entry.PartId);
        Assert.IsTrue(expanded[^1].IsStartMarker);
    }

    [TestMethod]
    public void ReportedStatus_AppendsOnlyEachEligibleRowsOwnSafeSummary()
    {
        var toolEntry = Tool("job", "m1", "tool1", 10, Now, Now, TimeSpan.FromMilliseconds(579));
        var tool = new LiveActivityHistoryRowViewModel(toolEntry, null, false, isGroupedChild: true);
        var toolDisplay = ActivityHistoryDisplayRowViewModel.Child(tool);
        Assert.AreEqual("shell · completed - Tool shell completed", toolDisplay.ReportedStatusText);
        Assert.AreEqual(toolDisplay.ReportedStatusText, toolDisplay.ReportedStatusTooltip);
        Assert.AreEqual("shell · completed", ActivityHistoryDisplayRowViewModel.Child(
            new LiveActivityHistoryRowViewModel(toolEntry with { Text = "" }, null, false, isGroupedChild: true))
            .ReportedStatusText);

        var stepStart = Step("job", "m1", "step1", 20, Now);
        var stepFinish = Step("job", "m1", "finish1", 30, Now.AddSeconds(1), reason: "tool-calls");
        var stepPair = new LiveActivityStepPair(stepStart, stepFinish, LiveActivityStepState.Paired);
        var step = new LiveActivityHistoryRowViewModel(
            stepStart with { Text = "Step response summary" }, null, false, step: stepPair, isGroupedChild: true);
        Assert.AreEqual("Step: tool-calls - Step response summary", ActivityHistoryDisplayRowViewModel.Child(step).ReportedStatusText);
        var stepWithout = new LiveActivityHistoryRowViewModel(
            stepStart with { Text = " " }, null, false, step: LiveActivityStepPair.Single(stepStart, LiveActivityStepState.UnmatchedStart), isGroupedChild: true);
        Assert.AreEqual("Step: unfinished", ActivityHistoryDisplayRowViewModel.Child(stepWithout).ReportedStatusText);

        var otherEntry = toolEntry with { PartId = "orphan", Text = "Orphan response", ToolName = null };
        var other = ActivityHistoryDisplayRowViewModel.Other(
            new LiveActivityHistoryRowViewModel(otherEntry, null, false));
        Assert.AreEqual("Tool shell completed - Orphan response", other.ReportedStatusText);
        Assert.AreEqual("Tool shell completed", ActivityHistoryDisplayRowViewModel.Other(
            new LiveActivityHistoryRowViewModel(otherEntry with { Text = "" }, null, false)).ReportedStatusText);

        var text = ActivityHistoryDisplayRowViewModel.Child(new LiveActivityHistoryRowViewModel(
            Text("job", "m1", "text1", 40, Now, "Text response"), null, false, isGroupedChild: true));
        var parent = ActivityHistoryDisplayRowViewModel.Message(new ActivityHistoryMessageGroup(text.Activity));
        Assert.AreEqual(text.Activity.ReportedStatusText, text.ReportedStatusText);
        Assert.AreEqual(parent.Activity.ReportedStatusText, parent.ReportedStatusText);
        Assert.IsFalse(toolDisplay.ReportedStatusText.Contains("Orphan response", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Timing_ToolAndStepRowsNeverUseTextPartTiming()
    {
        var toolStart = Now.AddMinutes(-3);
        var tool = Tool(
            "job", "msg1", "tool1", 10, Now.AddMinutes(-2),
            toolStart: toolStart,
            duration: TimeSpan.FromSeconds(4),
            status: "completed",
            outcome: LiveActivityToolOutcome.Completed);
        var toolChild = new LiveActivityHistoryRowViewModel(
            tool, null, false, isGroupedChild: true);
        Assert.AreEqual(toolStart, toolChild.StartedSortTimestamp);
        Assert.AreEqual(4d, toolChild.DurationSortSeconds, 0.001);
        Assert.AreEqual("completed", toolChild.Entry.ToolStatus);

        var start = Step("job", "msg1", "s1", 20, Now.AddMinutes(-2));
        var finish = Step("job", "msg1", "f1", 30, Now.AddMinutes(-1), reason: "stop");
        var pair = new LiveActivityStepPair(start, finish, LiveActivityStepState.Paired);
        var stepChild = new LiveActivityHistoryRowViewModel(
            start, null, false, step: pair, isGroupedChild: true);
        Assert.AreEqual(Now.AddMinutes(-2), stepChild.StartedSortTimestamp);
        Assert.AreEqual(TimeSpan.FromMinutes(1).TotalSeconds, stepChild.DurationSortSeconds, 0.001);
        Assert.AreEqual("Step: stop", stepChild.ReportedStatusText);
    }

    [TestMethod]
    public void Reader_ParsesTextPartTimeOnlyAsOneValidatedPair()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            TextRecord(10_000, "p1", "msg1", start: 1_000, end: 3_000),
            TextRecord(20_000, "p2", "msg1", start: 5_000, end: 4_000),
            TextRecord(30_000, "p3", "msg1", start: 6_000, end: null),
            TextRecord(40_000, "p4", "msg1", start: 7_000, end: 9_000, duplicateStart: true),
            "{\"type\":\"text\",\"timestamp\":50000,\"sessionID\":\"ses\",\"role\":\"assistant\"," +
            "\"part\":{\"id\":\"p5\",\"messageID\":\"msg1\",\"type\":\"text\",\"text\":\"time\"," +
            "\"time\":123}}");

        var prepared = Prepare(workspace);
        var activity = Read(workspace, prepared);
        Assert.IsTrue(activity.IsHealthy);

        var valid = activity.History.Single(entry => entry.PartId == "p1");
        Assert.AreEqual(1_000, valid.TextStartUnixMilliseconds);
        Assert.AreEqual(3_000, valid.TextEndUnixMilliseconds);
        Assert.AreEqual(10_000, valid.SourceTimestamp!.Value.ToUnixTimeMilliseconds(),
            "The record root timestamp stays the provenance time.");

        foreach (var part in new[] { "p2", "p3", "p4", "p5" })
        {
            var entry = activity.History.Single(item => item.PartId == part);
            Assert.IsNull(entry.TextStartUnixMilliseconds, part);
            Assert.IsNull(entry.TextEndUnixMilliseconds, part);
        }
    }

    [TestMethod]
    public void Grouping_V1AndV2ToolRecordsKeepTheirOwnTimingUnderATextParent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            TextRecord(10_000, "p1", "msg1", start: null, end: null),
            ToolRecordV1(11_000, "v1", "msg1", 11_000, 12_000),
            ToolRecordV2(13_000, "v2", "call-v2", "msg1", 13_000, 16_000),
            TestWorkspace.StepStart(17_000, session: "ses", part: "s1", message: "msg1"),
            TestWorkspace.StepFinish(18_000, 0.01m, "stop", session: "ses", part: "f1", message: "msg1"));

        var prepared = Prepare(workspace);
        var activity = Read(workspace, prepared);
        var row = new JobRowViewModel(Snapshot("job", activity.History) with
        {
            LiveActivity = activity,
            SessionId = "ses"
        });

        var group = row.ActivityHistoryMessageGroups.Single();
        Assert.AreEqual("p1", group.Message.Entry.PartId);
        Assert.AreEqual(0, row.OtherActivityRows.Count);

        var v1 = group.Children.Single(child => child.Entry.PartId == "v1");
        Assert.IsTrue(v1.IsToolActivity);
        Assert.AreEqual("shell", v1.Entry.ToolName);
        Assert.AreEqual("completed", v1.Entry.ToolStatus);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(11_000),
            v1.StartedSortTimestamp);
        Assert.AreEqual(1d, v1.DurationSortSeconds, 0.001);

        var v2 = group.Children.Single(child => child.Entry.PartId == "v2");
        Assert.IsTrue(v2.IsToolActivity);
        Assert.AreEqual("completed", v2.Entry.ToolStatus);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(13_000), v2.StartedSortTimestamp);
        Assert.AreEqual(3d, v2.DurationSortSeconds, 0.001);

        var step = group.Children.Single(child => child.IsStepActivity);
        Assert.AreEqual(LiveActivityStepState.Paired, step.StepState);
        Assert.AreEqual("Step: stop", step.ReportedStatusText);
        Assert.AreEqual(1d, step.DurationSortSeconds, 0.001);
    }

    [TestMethod]
    public void Selection_ParentAndChildKeysAreDistinctAndCollapseKeepsTheIdentity()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        var childP1 = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p1");
        var childP2 = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p2");

        Assert.AreNotEqual(parent.Key, childP2.Key);
        Assert.AreEqual(parent.Activity.Entry.PartId, childP2.Activity.Entry.PartId);
        Assert.IsTrue(parent.Key.IsMessage);
        Assert.IsFalse(childP2.Key.IsMessage);

        job.SelectedActivity = childP1.Activity;
        Assert.AreEqual("p1", job.SelectedActivity!.Entry.PartId);

        // A filter change hides/reveals children but the selected source
        // identity in the still-visible message stays coherent.
        viewModel.ShowActivityTextFilter = false;
        Assert.AreEqual("p1", job.SelectedActivity!.Entry.PartId);
        viewModel.ShowActivityTextFilter = true;

        // Collapse does not silently retarget the selected child.
        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = false;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual("p1", job.SelectedActivity!.Entry.PartId);

        // Switching jobs resets filters and expansion for both jobs.
        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = Now,
            State = MonitoringState.Monitoring,
            Jobs =
            [
                Snapshot("job", entries),
                Snapshot("jobB", [Text("jobB", "msgB", "b1", 100, Now, "b")])
            ],
            Warnings = Array.Empty<string>()
        });
        viewModel.SelectedJob = viewModel.Jobs.Single(item => item.JobId == "jobB");
        viewModel.SelectedJob = viewModel.Jobs.Single(item => item.JobId == "job");
        Assert.IsTrue(viewModel.ShowActivityTextFilter);
        Assert.IsFalse(
            viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage).IsExpanded);
    }

    [TestMethod]
    public void Selection_ParentMessageAndRepresentativeChildSurviveRepresentativeChanges()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();

        // Select the representative child copy, not the parent.
        var representativeChild = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p2");
        viewModel.SelectedActivityDisplayRow = representativeChild;
        Assert.AreEqual(representativeChild.Key, job.SelectedDisplayRowKey);
        Assert.AreEqual("p2", job.SelectedActivity!.Entry.PartId);

        // p3 becomes the new representative; p2 stays a child with its own
        // physical identity, so the child selection is not transferred to p3.
        Apply(viewModel, Snapshot("job",
            entries.Append(Text("job", "msg1", "p3", 30, Now, "three")).ToArray()));
        Assert.AreEqual("p2", job.SelectedActivity!.Entry.PartId);
        Assert.IsFalse(job.SelectedDisplayRowKey!.Value.IsMessage);
        Assert.AreEqual("p2", viewModel.SelectedActivityDisplayRow!.Activity.Entry.PartId);
        Assert.AreEqual("p3", job.ActivityHistoryMessageGroups.Single().Message.Entry.PartId);

        // Select the parent message. Its identity is the message, so a later
        // representative change restores the new parent rather than p3.
        var parentRow = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        viewModel.SelectedActivityDisplayRow = parentRow;
        Assert.IsTrue(job.SelectedDisplayRowKey!.Value.IsMessage);
        Assert.AreEqual(string.Empty, job.SelectedDisplayRowKey.Value.PartId);
        Apply(viewModel, Snapshot("job",
            entries.Append(Text("job", "msg1", "p3", 30, Now, "three"))
                .Append(Text("job", "msg1", "p4", 40, Now.AddSeconds(1), "four"))
                .ToArray()));
        Assert.IsTrue(job.SelectedDisplayRowKey!.Value.IsMessage);
        Assert.AreEqual(string.Empty, job.SelectedDisplayRowKey.Value.PartId);
        Assert.AreEqual("p4", job.SelectedActivity!.Entry.PartId);
        Assert.AreEqual("p4", viewModel.SelectedActivityDisplayRow!.Activity.Entry.PartId);
    }

    [TestMethod]
    public void Selection_ChildKeySurvivesCollapseAndFilterAndReturnsWhenRevealed()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        var child = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p1");
        viewModel.SelectedActivityDisplayRow = child;
        var childKey = job.SelectedDisplayRowKey!.Value;

        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = false;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsTrue(job.IsDisplaySelectionPending);
        Assert.AreEqual("p1", job.SelectedActivity!.Entry.PartId);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsMessage);
        Assert.AreEqual("p2", viewModel.SelectedActivityDisplayRow.Activity.Entry.PartId);

        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.AreEqual("p1", viewModel.SelectedActivityDisplayRow!.Activity.Entry.PartId);

        viewModel.ShowActivityTextFilter = false;
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsTrue(job.IsDisplaySelectionPending);
        Assert.AreEqual("p1", job.SelectedActivity!.Entry.PartId);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsMessage);
        viewModel.ShowActivityTextFilter = true;
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.AreEqual("p1", viewModel.SelectedActivityDisplayRow!.Activity.Entry.PartId);
    }

    [TestMethod]
    public void Selection_RepresentativeChildKeepsPendingIdentityAcrossTemporaryHide()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        var representativeChild = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p2");
        viewModel.SelectedActivityDisplayRow = representativeChild;
        var childKey = job.SelectedDisplayRowKey!.Value;
        Assert.IsFalse(job.IsDisplaySelectionPending);

        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = false;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsTrue(job.IsDisplaySelectionPending);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsMessage);
        Assert.AreEqual("p2", viewModel.SelectedActivityDisplayRow.Activity.Entry.PartId);

        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsChild);
        Assert.AreEqual("p2", viewModel.SelectedActivityDisplayRow.Activity.Entry.PartId);

        viewModel.ShowActivityTextFilter = false;
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsTrue(job.IsDisplaySelectionPending);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsMessage);
        viewModel.ShowActivityTextFilter = true;
        Assert.AreEqual(childKey, job.SelectedDisplayRowKey);
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.IsTrue(viewModel.SelectedActivityDisplayRow!.IsChild);
        Assert.AreEqual("p2", viewModel.SelectedActivityDisplayRow.Activity.Entry.PartId);
    }

    [TestMethod]
    public void Selection_ExplicitDifferentRowWhileChildHiddenDiscardsPending()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two"),
            Text("job", "msg2", "p3", 30, Now, "other")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        var firstParent = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsMessage && item.Activity.Entry.MessageId == "msg1");
        firstParent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        var child = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p1");
        viewModel.SelectedActivityDisplayRow = child;
        var childKey = job.SelectedDisplayRowKey!.Value;

        firstParent = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsMessage && item.Activity.Entry.MessageId == "msg1");
        firstParent.IsExpanded = false;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.IsTrue(job.IsDisplaySelectionPending);

        // A different visible parent is an explicit user selection and replaces
        // the pending hidden child.
        var otherParent = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsMessage && item.Activity.Entry.MessageId == "msg2");
        viewModel.SelectedActivityDisplayRow = otherParent;
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.AreNotEqual(childKey, job.SelectedDisplayRowKey);
        Assert.AreEqual("msg2", job.SelectedDisplayRowKey!.Value.MessageId);
        Assert.AreEqual("p3", job.SelectedActivity!.Entry.PartId);

        firstParent = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsMessage && item.Activity.Entry.MessageId == "msg1");
        firstParent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.AreEqual("msg2", job.SelectedDisplayRowKey!.Value.MessageId);
        Assert.AreEqual(
            "msg2",
            viewModel.SelectedActivityDisplayRow!.Activity.Entry.MessageId);
    }

    [TestMethod]
    public void Selection_PendingChildIsDiscardedOnJobSwitch()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel,
            Snapshot("job", entries),
            Snapshot("jobB", [Text("jobB", "msgB", "b1", 100, Now, "b")]));
        var job = viewModel.Jobs.Single(item => item.JobId == "job");
        viewModel.SelectedJob = job;
        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        viewModel.RefreshActivityHistoryDisplayRows();
        var child = viewModel.ActivityHistoryDisplayRows.Single(item =>
            item.IsChild && item.Activity.Entry.PartId == "p1");
        viewModel.SelectedActivityDisplayRow = child;
        parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = false;
        viewModel.RefreshActivityHistoryDisplayRows();
        Assert.IsTrue(job.IsDisplaySelectionPending);

        viewModel.SelectedJob = viewModel.Jobs.Single(item => item.JobId == "jobB");
        viewModel.SelectedJob = job;
        Assert.IsFalse(job.IsDisplaySelectionPending);
        Assert.IsNull(job.SelectedDisplayRowKey);
    }

    [TestMethod]
    public void DurationSort_OrdersTopLevelBlocksAndReflattenChildrenChronologically()
    {
        var message1 = new LiveActivityHistoryRowViewModel(
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            null,
            false,
            messageDuration: MessageDuration("msg1", TimeSpan.FromSeconds(10)));
        var message2 = new LiveActivityHistoryRowViewModel(
            Text("job", "msg2", "p2", 20, Now.AddMinutes(-1), "two"),
            null,
            false,
            messageDuration: MessageDuration("msg2", TimeSpan.FromSeconds(20)));
        var childText = new LiveActivityHistoryRowViewModel(
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            null,
            false,
            messageDuration: MessageDuration("msg1", TimeSpan.FromSeconds(10)),
            isGroupedChild: true);
        var childTool = new LiveActivityHistoryRowViewModel(
            Tool("job", "msg1", "tool1", 30, Now, toolStart: Now, duration: TimeSpan.FromMinutes(5)),
            null,
            false,
            isGroupedChild: true);
        var groups = new[]
        {
            new ActivityHistoryMessageGroup(message1, [childText, childTool]),
            new ActivityHistoryMessageGroup(message2)
        };

        // Expand the first message through the cached display row, then sort
        // the same groups.
        var seed = ActivityHistoryDisplayRowViewModel.Project(
            groups, Array.Empty<LiveActivityHistoryRowViewModel>(),
            showText: true, showTools: true, showSteps: true, showOther: false);
        seed.Single(item => item.IsMessage && item.Activity.Entry.MessageId == "msg1")
            .IsExpanded = true;

        Assert.AreEqual(10d, message1.DurationSortSeconds, 0.001, "msg1 duration");
        Assert.AreEqual(20d, message2.DurationSortSeconds, 0.001, "msg2 duration");
        var ascending = ActivityHistoryDisplayRowViewModel.Project(
            groups, Array.Empty<LiveActivityHistoryRowViewModel>(),
            showText: true, showTools: true, showSteps: true, showOther: false,
            durationSort: ListSortDirection.Ascending);
        CollectionAssert.AreEqual(
            new[] { "msg1", "msg2" },
            ascending.Where(item => item.IsMessage)
                .Select(item => item.Activity.Entry.MessageId).ToArray());
        Assert.AreEqual(4, ascending.Count);
        Assert.AreEqual("p1", ascending[1].Activity.Entry.PartId);
        Assert.AreEqual("tool1", ascending[2].Activity.Entry.PartId);

        var descending = ActivityHistoryDisplayRowViewModel.Project(
            groups, Array.Empty<LiveActivityHistoryRowViewModel>(),
            showText: true, showTools: true, showSteps: true, showOther: false,
            durationSort: ListSortDirection.Descending);
        CollectionAssert.AreEqual(
            new[] { "msg2", "msg1" },
            descending.Where(item => item.IsMessage)
                .Select(item => item.Activity.Entry.MessageId).ToArray());
        // The long child tool stays under msg1 and chronological rather than
        // escaping its message or sorting the flat grid by its own duration.
        Assert.AreEqual(4, descending.Count);
        Assert.AreEqual("msg1", descending[1].Activity.Entry.MessageId);
        Assert.AreEqual("p1", descending[2].Activity.Entry.PartId);
        Assert.AreEqual("tool1", descending[3].Activity.Entry.PartId);
    }

    [TestMethod]
    public void ExpandedState_SurvivesFiltersAndRefreshForTheSameMessageIdentity()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
            Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
        };

        using var viewModel = ViewModel();
        Apply(viewModel, Snapshot("job", entries));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
        parent.IsExpanded = true;
        Assert.IsTrue(viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage).IsExpanded);

        viewModel.ShowActivityToolsFilter = false;
        viewModel.ShowActivityStepsFilter = false;
        Assert.IsTrue(viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage).IsExpanded);

        // A refresh rebuilds row objects and groups; the exact message identity
        // carries the user's expanded state forward.
        Apply(viewModel, Snapshot("job", entries));
        Assert.IsTrue(
            viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage).IsExpanded);

        // A newer text part changes the representative but not the message
        // identity, so the expanded state remains attached to the message.
        Apply(viewModel, Snapshot("job",
            entries.Append(Text("job", "msg1", "p3", 30, Now, "three")).ToArray()));
        var group = job.ActivityHistoryMessageGroups.Single();
        Assert.AreEqual("p3", group.Message.Entry.PartId);
        Assert.IsTrue(
            viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage).IsExpanded);
    }

    [TestMethod]
    public void Grouping_ConflictingStepRecordsStayAmbiguousChildrenAndNeverOther()
    {
        var entries = new[]
        {
            Text("job", "msg1", "p1", 10, Now.AddMinutes(-3), "text"),
            Step("job", "msg1", "st", 20, Now.AddMinutes(-2), conflict: true),
            Step("job", "msg1", "st", 30, Now.AddMinutes(-1), reason: "stop", conflict: true)
        };

        var row = new JobRowViewModel(Snapshot("job", entries));
        var group = row.ActivityHistoryMessageGroups.Single();
        Assert.AreEqual("p1", group.Message.Entry.PartId);
        Assert.AreEqual(0, row.OtherActivityRows.Count);

        var steps = group.Children.Where(child => child.IsStepActivity).ToArray();
        Assert.AreEqual(2, steps.Length);
        Assert.IsTrue(steps.All(step => step.StepState == LiveActivityStepState.Ambiguous));
        Assert.IsTrue(steps.All(step => step.ReportedStatusText == "Step: ambiguous"));
        Assert.IsFalse(steps.Any(step => step.StepState == LiveActivityStepState.Paired));
    }

    [STATestClass]
    public sealed class RenderedSelectionTests
    {
        [TestMethod]
        public void Grid_DurationHeaderSortsGroupedBlocksThroughModel()
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
                window.Show();
                PumpDispatcher();
                Apply(viewModel, Snapshot("job",
                    [
                        Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
                        Tool("job", "msg1", "tool1", 20, Now.AddMinutes(-1), toolStart: Now.AddMinutes(-1), duration: TimeSpan.FromSeconds(2)),
                        Text("job", "msg2", "p2", 30, Now, "two")
                    ]));
                viewModel.SelectedJob = viewModel.Jobs.Single();
                PumpDispatcher();
                var parentRow = viewModel.ActivityHistoryDisplayRows.Single(item =>
                    item.IsMessage && item.Activity.Entry.MessageId == "msg1");
                parentRow.IsExpanded = true;
                viewModel.RefreshActivityHistoryDisplayRows();
                PumpDispatcher();
                var grid = (DataGrid)window.FindName("LiveActivityHistoryGrid");
                var durationColumn = grid.Columns[2];
                var sortHandler = typeof(MainWindow).GetMethod(
                    "LiveActivityHistoryGrid_Sorting",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(sortHandler);
                var args = new DataGridSortingEventArgs(durationColumn);
                sortHandler.Invoke(window, [grid, args]);
                PumpDispatcher();
                Assert.IsTrue(args.Handled);
                Assert.AreEqual(ListSortDirection.Ascending, durationColumn.SortDirection);
                Assert.AreEqual(ListSortDirection.Ascending, viewModel.ActivityHistoryDurationSort);
                var rows = viewModel.ActivityHistoryDisplayRows;
                Assert.AreEqual(2, rows.Count(item => item.IsMessage));
                Assert.IsTrue(rows.Any(item => item.IsChild && item.Activity.Entry.PartId == "tool1"));

                var descending = new DataGridSortingEventArgs(durationColumn);
                sortHandler.Invoke(window, [grid, descending]);
                PumpDispatcher();
                Assert.AreEqual(ListSortDirection.Descending, durationColumn.SortDirection);
                Assert.AreEqual(ListSortDirection.Descending, viewModel.ActivityHistoryDurationSort);
            }
            finally
            {
                window.Close();
            }
        }

        [TestMethod]
        public void Grid_RestoresRepresentativeChildAndParentAcrossSnapshot()
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
                window.Show();
                PumpDispatcher();

                Apply(viewModel, Snapshot("job",
                    [
                        Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
                        Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two")
                    ]));
                var job = viewModel.Jobs.Single();
                viewModel.SelectedJob = job;
                PumpDispatcher();

                var parent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
                parent.IsExpanded = true;
                viewModel.RefreshActivityHistoryDisplayRows();
                PumpDispatcher();
                var child = viewModel.ActivityHistoryDisplayRows.Single(item =>
                    item.IsChild && item.Activity.Entry.PartId == "p2");
                viewModel.SelectedActivityDisplayRow = child;
                PumpDispatcher();

                var grid = (DataGrid)window.FindName("LiveActivityHistoryGrid");
                Assert.IsNotNull(grid);
                window.UpdateLayout();
                Assert.AreSame(
                    viewModel.SelectedActivityDisplayRow,
                    grid.SelectedItem,
                    $"model={viewModel.SelectedActivityDisplayRow?.Activity.Entry.PartId ?? "null"}; " +
                    $"grid={(grid.SelectedItem as ActivityHistoryDisplayRowViewModel)?.Activity.Entry.PartId ?? "null"}");

                // Representative changes; the physical child row is restored.
                Apply(viewModel, Snapshot("job",
                    [
                        Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
                        Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two"),
                        Text("job", "msg1", "p3", 30, Now, "three")
                    ]));
                window.UpdateLayout();
                PumpDispatcher();
                var restoredChild = viewModel.ActivityHistoryDisplayRows.Single(item =>
                    item.IsChild && item.Activity.Entry.PartId == "p2");
                Assert.AreSame(restoredChild, viewModel.SelectedActivityDisplayRow);
                Assert.AreSame(viewModel.SelectedActivityDisplayRow, grid.SelectedItem);
                Assert.AreEqual("p2", job.SelectedActivity!.Entry.PartId);

                // A parent selection uses message identity, so the new
                // representative becomes the selected parent row.
                var restoredParent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
                viewModel.SelectedActivityDisplayRow = restoredParent;
                PumpDispatcher();
                Apply(viewModel, Snapshot("job",
                    [
                        Text("job", "msg1", "p1", 10, Now.AddMinutes(-2), "one"),
                        Text("job", "msg1", "p2", 20, Now.AddMinutes(-1), "two"),
                        Text("job", "msg1", "p3", 30, Now.AddSeconds(-1), "three"),
                        Text("job", "msg1", "p4", 40, Now, "four")
                    ]));
                window.UpdateLayout();
                PumpDispatcher();
                var newParent = viewModel.ActivityHistoryDisplayRows.Single(item => item.IsMessage);
                Assert.AreEqual("p4", newParent.Activity.Entry.PartId);
                Assert.AreSame(newParent, viewModel.SelectedActivityDisplayRow);
                Assert.AreSame(viewModel.SelectedActivityDisplayRow, grid.SelectedItem);
                Assert.IsTrue(job.SelectedDisplayRowKey!.Value.IsMessage);
            }
            finally
            {
                window.Close();
            }
        }
    }

    // ---------------- fixtures ----------------

    internal static MainViewModel ViewModel() =>
        new(
            () => throw new InvalidOperationException("Monitoring is not started."),
            Dispatcher.CurrentDispatcher,
            Path.Combine(Path.GetTempPath(), "DSMonitor.Tests", "v2-grouping"),
            TimeSpan.FromSeconds(5));

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void Apply(MainViewModel viewModel, params ForgeJobSnapshot[] jobs) =>
        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = Now,
            State = MonitoringState.Monitoring,
            Jobs = jobs,
            Warnings = Array.Empty<string>()
        });

    internal static OpenCodeMessageDuration MessageDuration(string messageId, TimeSpan duration) =>
        new(
            messageId,
            Now.AddMinutes(-10),
            Now.AddMinutes(-10) + duration,
            duration);

    internal static LiveActivityHistoryEntry Text(
        string jobId,
        string message,
        string partId,
        long offset,
        DateTimeOffset? timestamp,
        string text,
        DateTimeOffset? textStart = null,
        DateTimeOffset? textEnd = null,
        bool conflict = false,
        string session = "ses") =>
        new(
            jobId,
            session,
            message,
            partId,
            "Assistant-reported text",
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            "D" + partId,
            null)
        {
            ActivityKind = LiveActivityKind.Text,
            TextStartUnixMilliseconds = textStart?.ToUnixTimeMilliseconds(),
            TextEndUnixMilliseconds = textEnd?.ToUnixTimeMilliseconds(),
            StepIdentityConflict = conflict
        };

    internal static LiveActivityHistoryEntry Tool(
        string jobId,
        string message,
        string partId,
        long offset,
        DateTimeOffset? timestamp,
        DateTimeOffset? toolStart,
        TimeSpan? duration,
        string status = "completed",
        LiveActivityToolOutcome outcome = LiveActivityToolOutcome.Completed,
        string session = "ses")
    {
        var start = toolStart?.ToUnixTimeMilliseconds();
        var end = start is not null && duration is not null
            ? start + (long)duration.Value.TotalMilliseconds
            : null;
        return new LiveActivityHistoryEntry(
            jobId,
            session,
            message,
            partId,
            "Observed tool activity",
            "Tool shell completed",
            SourcePath,
            offset,
            timestamp,
            Now,
            "D" + partId,
            duration)
        {
            ActivityKind = LiveActivityKind.Tool,
            ToolName = "shell",
            ToolStatus = status,
            ToolOutcome = outcome,
            ToolStartUnixMilliseconds = start,
            DurationStartUnixMilliseconds = start,
            DurationEndUnixMilliseconds = end
        };
    }

    internal static LiveActivityHistoryEntry Step(
        string jobId,
        string message,
        string partId,
        long offset,
        DateTimeOffset? timestamp,
        string? reason = null,
        bool conflict = false,
        string session = "ses") =>
        new(
            jobId,
            session,
            message,
            partId,
            reason is null ? "Observed step start" : "Observed step finish",
            reason is null ? "Session step started" : "Step finished",
            SourcePath,
            offset,
            timestamp,
            Now,
            "D" + partId,
            null)
        {
            ActivityKind = reason is null ? LiveActivityKind.StepStart : LiveActivityKind.StepFinish,
            StepFinishReason = reason,
            StepIdentityConflict = conflict
        };

    internal static ForgeJobSnapshot Snapshot(
        string jobId,
        IReadOnlyList<LiveActivityHistoryEntry> entries) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = jobId,
                MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                LogPath = SourcePath,
                HasExplicitLogPath = true,
                Started = Started,
                ExplicitStatus = ForgeJobStatus.Running
            },
            Status = ForgeJobStatus.Running,
            SessionId = "ses",
            LastActivity = Started.AddMinutes(5),
            ObservedAt = Now,
            LiveActivity = new LiveActivitySnapshot
            {
                History = entries,
                Availability = "Bounded synthetic activity.",
                IsHealthy = true
            }
        };

    private static string ToolRecordV1(
        long timestamp,
        string part,
        string message,
        long start,
        long end) =>
        "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
        ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"" + part +
        "\",\"messageID\":\"" + message + "\",\"type\":\"tool\",\"tool\":\"shell\"," +
        "\"state\":{\"status\":\"completed\",\"time\":{\"start\":" + start +
        ",\"end\":" + end + "}}}}";

    private static string ToolRecordV2(
        long timestamp,
        string part,
        string callId,
        string message,
        long start,
        long end) =>
        "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
        ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"" + callId +
        "\",\"partID\":\"" + part + "\",\"messageID\":\"" + message +
        "\",\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"completed\"," +
        "\"time\":{\"start\":" + start + ",\"end\":" + end + "}}}}";

    private static string TextRecord(
        long timestamp,
        string part,
        string message,
        long? start,
        long? end,
        bool duplicateStart = false)
    {
        var partObject = new Dictionary<string, object?>
        {
            ["id"] = part,
            ["messageID"] = message,
            ["type"] = "text",
            ["text"] = "synthetic"
        };
        if (duplicateStart)
        {
            // Handwritten duplicate property; the reader must reject it rather
            // than resolve last-key-wins.
            return "{\"type\":\"text\",\"timestamp\":" + timestamp +
                   ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"" + part +
                   "\",\"messageID\":\"" + message + "\",\"type\":\"text\",\"text\":\"synthetic\"," +
                   "\"time\":{\"start\":" + start + ",\"start\":" + end + ",\"end\":9000}}}";
        }

        if (start is not null || end is not null)
        {
            partObject["time"] = new Dictionary<string, object?>
            {
                ["start"] = start,
                ["end"] = end
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["timestamp"] = timestamp,
            ["sessionID"] = "ses",
            ["role"] = "assistant",
            ["part"] = partObject
        });
    }

    private static Prepared Prepare(TestWorkspace workspace)
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == "job");
        var database = Path.Combine(workspace.Root, "v2-activity.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var result = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation);
        return new Prepared(job, evidence[job.JobId], result.SourceObservation!);
    }

    private static LiveActivitySnapshot Read(TestWorkspace workspace, Prepared prepared) =>
        JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now);

    private sealed record Prepared(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
