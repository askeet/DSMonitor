using DSMonitor.Models;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class JobTableLiveActivityTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Duration_ActiveJobTicksAboutOncePerSecondAndFreezesWhenFinished()
    {
        var running = Snapshot(
            status: ForgeJobStatus.Running,
            started: Started,
            lastActivity: Started.AddSeconds(5),
            observedAt: Started.AddSeconds(10));
        var row = new JobRowViewModel(running);

        Assert.AreEqual("10s", row.DurationText);
        Assert.IsTrue(row.DurationUsesLiveClock);
        Assert.IsTrue(row.IsActive);

        row.RefreshDuration(Started.AddSeconds(65));
        Assert.AreEqual("1m 5s", row.DurationText);

        row.Update(
            Snapshot(
                status: ForgeJobStatus.Done,
                started: Started,
                lastActivity: Started.AddSeconds(70),
                observedAt: Started.AddSeconds(71)),
            Started.AddSeconds(71));
        Assert.AreEqual("1m 10s", row.DurationText);
        Assert.IsTrue(row.DurationHasReportedCompletion);
        Assert.IsFalse(row.DurationUsesLiveClock);

        row.RefreshDuration(Started.AddDays(1));
        Assert.AreEqual("1m 10s", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "Frozen");
    }

    [DataTestMethod]
    [DataRow(ForgeJobStatus.Running)]
    [DataRow(ForgeJobStatus.Blocked)]
    public void DurationAndSpinnerShareRunningAndBlockedDecision(ForgeJobStatus status)
    {
        var row = new JobRowViewModel(Snapshot(
            status,
            Started,
            Started.AddSeconds(4),
            Started.AddSeconds(5)));

        Assert.IsTrue(row.IsActive);
        Assert.IsTrue(row.DurationUsesLiveClock);
        Assert.AreEqual("5s", row.DurationText);

        var activeNotified = false;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(JobRowViewModel.IsActive))
            {
                activeNotified = true;
            }
        };
        row.SetMonitoringActive(false);
        var frozen = row.DurationText;
        row.RefreshDuration(Started.AddMinutes(1));

        Assert.IsTrue(activeNotified);
        Assert.IsFalse(row.IsActive);
        Assert.IsFalse(row.DurationUsesLiveClock);
        Assert.AreEqual(frozen, row.DurationText);
    }

    [TestMethod]
    public void DurationAndSpinnerShareFreshUnknownActivityAndClockExpiry()
    {
        var sourceTime = Started.AddMinutes(5);
        var row = new JobRowViewModel(Snapshot(
            ForgeJobStatus.Unknown,
            Started,
            sourceTime,
            sourceTime.AddSeconds(1),
            doing: TextActivity("tool call", 200, sourceTime)));

        Assert.IsTrue(row.IsActive, "The first fresh job-specific activity is observed as active.");
        Assert.IsTrue(row.DurationUsesLiveClock);
        Assert.AreEqual(301d, row.DurationSortSeconds);

        var activeNotified = false;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(JobRowViewModel.IsActive))
            {
                activeNotified = true;
            }
        };
        row.RefreshDuration(sourceTime.AddSeconds(31));

        Assert.IsTrue(activeNotified);
        Assert.IsFalse(row.IsActive);
        Assert.IsFalse(row.DurationUsesLiveClock);
        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.AreEqual(300d, row.DurationSortSeconds);

        var missingTimestamp = Snapshot(
            ForgeJobStatus.Unknown,
            Started,
            lastActivity: null,
            observedAt: sourceTime.AddSeconds(32));
        row.Update(missingTimestamp, sourceTime.AddSeconds(32));
        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.AreEqual(300d, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "retained");

        var invalidTimestamp = Snapshot(
            ForgeJobStatus.Unknown,
            Started,
            Started.AddSeconds(-1),
            sourceTime.AddSeconds(33));
        row.Update(invalidTimestamp, sourceTime.AddSeconds(33));
        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.AreEqual(300d, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "retained");
    }

    [TestMethod]
    public void Duration_UnknownJobUsesObservedSpanAndNeverTicksForever()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Unknown,
            started: Started,
            lastActivity: Started.AddMinutes(5),
            observedAt: Started.AddMinutes(1)));

        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.AreEqual(JobDurationBasis.ObservedSpan, row.DurationBasis);
        Assert.IsFalse(row.IsActive);

        row.RefreshDuration(Started.AddHours(2));

        Assert.AreEqual("5m 0s", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "observed span");
        StringAssert.Contains(row.DurationTooltip, "not proof of completion");
        StringAssert.Contains(row.DurationTooltip, "No live timer");
    }

    [TestMethod]
    public void Duration_UnknownJobWithLatestDoneActivityFreezesAsHeuristicOnly()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Unknown,
            started: Started,
            lastActivity: Started.AddMinutes(10),
            observedAt: Started.AddMinutes(10),
            doing: TextActivity("finished result", offset: 100, timestamp: Started.AddMinutes(10)),
            done: TextActivity("finished result", offset: 100, timestamp: Started.AddMinutes(10))));

        Assert.AreEqual("10m 0s", row.DurationText);
        Assert.AreEqual(JobDurationBasis.FinishedActivityHeuristic, row.DurationBasis);
        Assert.IsTrue(row.DurationHasHeuristicCompletion);
        Assert.IsFalse(row.DurationHasReportedCompletion);
        Assert.IsFalse(row.IsActive);

        row.RefreshDuration(Started.AddDays(1));

        Assert.AreEqual("10m 0s", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "heuristic only");
        StringAssert.Contains(row.DurationTooltip, "not authoritative completion");
    }

    [TestMethod]
    public void Duration_DoneFollowedByNewerActivityDoesNotFreezeTheRow()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Unknown,
            started: Started,
            lastActivity: Started.AddMinutes(8),
            observedAt: Started.AddMinutes(8),
            doing: TextActivity("later work", offset: 200, timestamp: Started.AddMinutes(8)),
            done: TextActivity("intermediate result", offset: 100, timestamp: Started.AddMinutes(5))));

        Assert.AreEqual("8m 0s", row.DurationText);
        Assert.AreEqual(JobDurationBasis.Live, row.DurationBasis);
        Assert.IsTrue(row.IsActive);
        Assert.IsFalse(row.DurationHasHeuristicCompletion);

        row.RefreshDuration(Started.AddDays(1));
        Assert.AreEqual("8m 0s", row.DurationText);
    }

    [TestMethod]
    public void Duration_DoneFromAnotherJobIsIgnored()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Unknown,
            started: Started,
            lastActivity: Started.AddMinutes(6),
            observedAt: Started.AddMinutes(6),
            doing: TextActivity("this job activity", offset: 100, timestamp: Started.AddMinutes(6)),
            done: TextActivity("another job result", offset: 100, timestamp: Started.AddMinutes(6), jobId: "other-job")));

        Assert.AreEqual(JobDurationBasis.Live, row.DurationBasis);
        Assert.IsTrue(row.IsActive);
        Assert.IsFalse(row.DurationHasHeuristicCompletion);

        row.RefreshDuration(Started.AddMinutes(6).AddSeconds(31));

        Assert.IsFalse(row.IsActive);
        Assert.AreEqual(JobDurationBasis.ObservedSpan, row.DurationBasis);
        Assert.IsFalse(row.DurationHasHeuristicCompletion);
        Assert.AreEqual("6m 0s", row.DurationText);
    }

    [TestMethod]
    public void Duration_ReportedCompletionUsesDoneSourceWhenLastActivityIsUnavailable()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Done,
            started: Started,
            lastActivity: null,
            observedAt: Started.AddMinutes(9),
            doing: TextActivity("reported finish", offset: 100, timestamp: Started.AddMinutes(4)),
            done: TextActivity("reported finish", offset: 100, timestamp: Started.AddMinutes(4))));

        Assert.AreEqual("4m 0s", row.DurationText);
        Assert.AreEqual(JobDurationBasis.FinishedReportedMetadata, row.DurationBasis);
        StringAssert.Contains(row.DurationTooltip, "finish");
    }

    [TestMethod]
    public void Duration_TerminalMetadataFreezesAtLastObservedActivity()
    {
        var row = new JobRowViewModel(Snapshot(
            status: ForgeJobStatus.Failed,
            started: Started,
            lastActivity: Started.AddMinutes(7),
            observedAt: Started.AddMinutes(7)));

        Assert.AreEqual("7m 0s", row.DurationText);
        Assert.AreEqual(JobDurationBasis.FinishedTerminalMetadata, row.DurationBasis);
        Assert.IsTrue(row.DurationHasReportedCompletion);

        row.RefreshDuration(Started.AddDays(1));

        Assert.AreEqual("7m 0s", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "Forge metadata reports FAILED");
    }

    private static ActivityExcerpt TextActivity(
        string text,
        long offset,
        DateTimeOffset timestamp,
        string jobId = "job_synthetic") =>
        new(
            text,
            "Assistant-reported text",
            jobId,
            "session_synthetic",
            "message_synthetic",
            "part_synthetic",
            @"C:\synthetic\runs\job.log",
            offset,
            timestamp,
            timestamp.AddSeconds(1));

    private static ForgeJobSnapshot Snapshot(
        ForgeJobStatus status,
        DateTimeOffset started,
        DateTimeOffset? lastActivity,
        DateTimeOffset observedAt,
        ActivityExcerpt? doing = null,
        ActivityExcerpt? done = null) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = "job_synthetic",
                MetadataPath = @"C:\synthetic\runs\job_synthetic.json",
                Directory = @"D:\Work\Synthetic",
                Started = started,
                ExplicitStatus = status
            },
            Status = status,
            LastActivity = lastActivity,
            ObservedAt = observedAt,
            LiveActivity = new LiveActivitySnapshot
            {
                Doing = doing,
                Done = done,
                Availability = "Bounded synthetic activity."
            }
        };
}
