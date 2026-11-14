using System.Globalization;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class DurationDisplayTests
{
    [TestMethod]
    public void Format_UsesTotalMinutesAndRoundedMilliseconds()
    {
        Assert.AreEqual("0.010s", Format(10));
        Assert.AreEqual("0.001s", Format(1));
        Assert.AreEqual("0.395s", Format(395));
        Assert.AreEqual("1.077s", Format(1_077));
        Assert.AreEqual("3.827s", Format(3_827));
        Assert.AreEqual("1m 0.000s", Format(60_000));
        Assert.AreEqual("1m 2.345s", Format(62_345));
        Assert.AreEqual("61m 1.250s", Format(3_661_250));
        Assert.AreEqual("0s", Format(0));
    }

    [TestMethod]
    public void Format_RoundsAcrossMinuteBoundaryAndHandlesMaximumTimeSpan()
    {
        Assert.AreEqual("59.994s", Format(59_994));
        Assert.AreEqual("59.995s", Format(59_995));
        Assert.AreEqual("59.999s", Format(59_999));
        Assert.AreEqual("1m 0.000s", DurationDisplayFormatter.Format(TimeSpan.FromTicks(599_995_000)));
        Assert.AreEqual(
            "15372286728m 5.478s",
            DurationDisplayFormatter.Format(TimeSpan.MaxValue));
        Assert.AreEqual(
            "N/A",
            DurationDisplayFormatter.Format(TimeSpan.FromMilliseconds(-1)));
    }

    [TestMethod]
    public void Format_IsInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.AreEqual("1m 2.345s", Format(62_345));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void Started_UsesEnglishMonthUnpaddedDayLocalTimeAndThreeFractionDigits()
    {
        var localTimestamp = new DateTimeOffset(
            2026, 11, 4, 22, 16, 3, 450,
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4, 22, 16, 3)));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.AreEqual("Nov 4 22:16:03.450", StartedDisplayFormatter.Format(localTimestamp));
            Assert.AreEqual("N/A", StartedDisplayFormatter.Format(null));
            Assert.AreEqual(
                "Nov 4 22:16:03.450",
                new LiveActivityHistoryRowViewModel(
                    Entry("started", LiveActivityKind.Text, localTimestamp),
                    null,
                    false).StartedText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public void JobDuration_UsesSharedFormatAndLiveUpdates()
    {
        var started = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        var now = started.AddMilliseconds(62_345);
        var row = new JobRowViewModel(Snapshot(
            started,
            now,
            ForgeJobStatus.Running));

        Assert.AreEqual("1m 2.345s", row.DurationText);
        row.RefreshDuration(now.AddSeconds(1));
        Assert.AreEqual("1m 3.345s", row.DurationText);
        Assert.AreEqual(63.345d, row.DurationSortSeconds, 0.0001);
    }

    [TestMethod]
    public void JobDuration_FinishedAndMissingUseSharedFormatOrNA()
    {
        var started = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        var finished = started.AddMilliseconds(62_345);
        var done = new JobRowViewModel(Snapshot(
            started,
            finished,
            ForgeJobStatus.Done,
            lastActivity: finished));
        Assert.AreEqual("1m 2.345s", done.DurationText);

        var missing = new JobRowViewModel(Snapshot(
            null,
            finished,
            ForgeJobStatus.Done,
            lastActivity: finished));
        Assert.AreEqual("N/A", missing.DurationText);
    }

    [TestMethod]
    public void ActivityHistoryDuration_UsesSharedFormatForTextToolAndStep()
    {
        var now = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        var textEntry = Entry("text-1", LiveActivityKind.Text, now);
        var text = new LiveActivityHistoryRowViewModel(
            textEntry,
            null,
            false,
            null,
            new OpenCodeMessageDuration(
                "msg",
                now.AddMilliseconds(-1_077),
                now,
                TimeSpan.FromMilliseconds(1_077)));
        Assert.AreEqual("1.077s", text.DurationText);

        var toolEntry = Entry(
            "tool-1",
            LiveActivityKind.Tool,
            now,
            TimeSpan.FromMilliseconds(62_345)) with
        {
            ToolName = "shell",
            ToolStatus = "completed",
            ToolOutcome = LiveActivityToolOutcome.Completed
        };
        Assert.AreEqual(
            "1m 2.345s",
            new LiveActivityHistoryRowViewModel(toolEntry, null, false).DurationText);

        var stepStart = Entry(
            "step-start",
            LiveActivityKind.StepStart,
            now.AddMilliseconds(-3_827));
        var stepFinish = Entry("step-finish", LiveActivityKind.StepFinish, now);
        var step = new LiveActivityHistoryRowViewModel(
            stepStart,
            null,
            false,
            null,
            null,
            new LiveActivityStepPair(
                stepStart,
                stepFinish,
                LiveActivityStepState.Paired),
            now,
            false);
        Assert.AreEqual("3.827s", step.DurationText);

        Assert.AreEqual(
            "N/A",
            new LiveActivityHistoryRowViewModel(textEntry, null, false).DurationText);
        var invalid = toolEntry with { ObservedDuration = TimeSpan.FromMilliseconds(-1) };
        Assert.AreEqual(
            "N/A",
            new LiveActivityHistoryRowViewModel(invalid, null, false).DurationText);
    }

    private static string Format(int milliseconds) =>
        DurationDisplayFormatter.Format(TimeSpan.FromMilliseconds(milliseconds));

    private static ForgeJobSnapshot Snapshot(
        DateTimeOffset? started,
        DateTimeOffset observedAt,
        ForgeJobStatus status,
        DateTimeOffset? lastActivity = null) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = "job",
                MetadataPath = @"C:\synthetic\job.json",
                LogPath = @"C:\synthetic\job.log",
                HasExplicitLogPath = true,
                Started = started
            },
            Status = status,
            LastActivity = lastActivity,
            ObservedAt = observedAt
        };

    private static LiveActivityHistoryEntry Entry(
        string partId,
        LiveActivityKind kind,
        DateTimeOffset timestamp,
        TimeSpan? duration = null) =>
        new(
            "job",
            "ses",
            "msg",
            partId,
            "Observed",
            "text",
            @"C:\synthetic\job.log",
            0,
            timestamp,
            timestamp,
            "D" + partId,
            duration)
        {
            ActivityKind = kind
        };
}
