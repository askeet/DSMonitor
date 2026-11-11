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
        Assert.AreEqual(JobDurationBasis.ObservedSpan, row.DurationBasis);
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

        Assert.AreEqual(JobDurationBasis.ObservedSpan, row.DurationBasis);
        Assert.IsFalse(row.DurationHasHeuristicCompletion);
        row.RefreshDuration(Started.AddDays(1));
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
