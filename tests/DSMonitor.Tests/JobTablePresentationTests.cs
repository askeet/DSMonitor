using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class JobTablePresentationTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Project_UsesFinalFolderAndPreservesFullPathInTooltip()
    {
        var row = new JobRowViewModel(Snapshot(
            directory: @"D:\Repositories\WPF\DSMonitor\"));

        Assert.AreEqual("DSMonitor", row.ProjectText);
        Assert.AreEqual(@"D:\Repositories\WPF\DSMonitor\", row.ProjectTooltip);

        var unicode = new JobRowViewModel(Snapshot(
            directory: @"D:\Work\Ünïcode Project\"));
        Assert.AreEqual("Ünïcode Project", unicode.ProjectText);
        Assert.AreEqual(@"D:\Work\Ünïcode Project\", unicode.ProjectTooltip);
    }

    [TestMethod]
    public void Project_RepeatedTrailingSeparators_ShowLeafAndPreserveFullPath()
    {
        var backslashes = new JobRowViewModel(Snapshot(directory: @"C:\repo\\"));
        Assert.AreEqual("repo", backslashes.ProjectText);
        Assert.AreEqual(@"C:\repo\\", backslashes.ProjectTooltip);

        var mixed = new JobRowViewModel(Snapshot(directory: "C:/repo///"));
        Assert.AreEqual("repo", mixed.ProjectText);
        Assert.AreEqual("C:/repo///", mixed.ProjectTooltip);
    }

    [DataTestMethod]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("   ", "   ")]
    public void Project_MissingDirectory_UsesNeutralNAAndOriginalTooltip(
        string? directory,
        string expectedTooltip)
    {
        var row = new JobRowViewModel(Snapshot(directory: directory));

        Assert.AreEqual("N/A", row.ProjectText);
        Assert.AreEqual(expectedTooltip, row.ProjectTooltip);
    }

    [DataTestMethod]
    [DataRow(@"C:\")]
    [DataRow(@"C:/")]
    [DataRow(@"C:\folder\bad:name")]
    [DataRow(@"C:\folder\bad|name")]
    [DataRow(@"\\server\share\")]
    public void Project_InvalidPath_UsesNeutralNAAndPreservesOriginalPath(string directory)
    {
        var row = new JobRowViewModel(Snapshot(directory: directory));

        Assert.AreEqual("N/A", row.ProjectText);
        Assert.AreEqual(directory, row.ProjectTooltip);
    }

    [TestMethod]
    public void Started_UsesExistingLocalConventionAndMissingShowsPlaceholder()
    {
        var row = new JobRowViewModel(Snapshot(started: Started));
        Assert.AreEqual(Started.ToLocalTime().ToString("MMM dd, HH:mm"), row.StartedText);

        var missing = new JobRowViewModel(Snapshot(started: null));
        Assert.AreEqual("—", missing.StartedText);
    }

    [TestMethod]
    public void Duration_IsLastActivityMinusStartedAndCompact()
    {
        var row = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddSeconds(3725)));

        Assert.AreEqual("1h 2m", row.DurationText);
        Assert.AreEqual(3725d, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "Observed duration 1h 2m 5s");
        StringAssert.Contains(row.DurationTooltip, "no live timer");
    }

    [TestMethod]
    public void Duration_EqualTimestamps_IsZeroAndValid()
    {
        var row = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started));

        Assert.AreEqual("0s", row.DurationText);
        Assert.AreEqual(0d, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "Observed duration 0s");
    }

    [TestMethod]
    public void Duration_NegativeSpan_IsInvalidNA()
    {
        var row = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(-5)));

        Assert.AreEqual("N/A", row.DurationText);
        Assert.AreEqual(double.PositiveInfinity, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "precedes started");
    }

    [TestMethod]
    public void Duration_MissingTimestamp_IsNotInferred()
    {
        var missingStarted = new JobRowViewModel(Snapshot(
            started: null,
            lastActivity: Started));
        Assert.AreEqual("N/A", missingStarted.DurationText);
        Assert.AreEqual(double.PositiveInfinity, missingStarted.DurationSortSeconds);
        StringAssert.Contains(missingStarted.DurationTooltip, "N/A");

        var missingActivity = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: null));
        Assert.AreEqual("N/A", missingActivity.DurationText);
        Assert.AreEqual(double.PositiveInfinity, missingActivity.DurationSortSeconds);
        StringAssert.Contains(missingActivity.DurationTooltip, "N/A");
    }

    [TestMethod]
    public void Duration_FinishedJobDoesNotAdvanceWithTheWallClock()
    {
        var snapshot = Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(5),
            status: ForgeJobStatus.Done);
        var row = new JobRowViewModel(snapshot);
        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.IsTrue(row.DurationHasReportedCompletion);

        row.Update(snapshot, Started.AddDays(30));

        Assert.AreEqual("5m 0s", row.DurationText);
        Assert.AreEqual(300d, row.DurationSortSeconds);
        StringAssert.Contains(row.DurationTooltip, "Observed duration 5m 0s");
        StringAssert.Contains(row.DurationTooltip, "Frozen");
    }

    [TestMethod]
    public void TableDuration_IsSeparateFromRightPanelElapsed()
    {
        var row = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(5),
            status: ForgeJobStatus.Running,
            observedAt: Started.AddMinutes(5)));

        row.RefreshElapsed(Started.AddMinutes(30));

        Assert.AreEqual("30m 0s", row.ElapsedText);
        Assert.AreEqual("5m 0s", row.DurationText);
    }

    [TestMethod]
    public void RightPanelElapsed_PreservesOriginalBehavior()
    {
        var running = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(1),
            status: ForgeJobStatus.Running));
        running.RefreshElapsed(Started.AddMinutes(10));
        Assert.AreEqual("10m 0s", running.ElapsedText);
        running.RefreshElapsed(Started.AddMinutes(70));
        Assert.AreEqual("1h 10m", running.ElapsedText);

        var done = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(5),
            status: ForgeJobStatus.Done));
        done.RefreshElapsed(Started.AddDays(1));
        Assert.AreEqual("5m 0s", done.ElapsedText);

        var reversed = new JobRowViewModel(Snapshot(
            started: Started,
            lastActivity: Started.AddMinutes(-5),
            status: ForgeJobStatus.Done));
        Assert.AreEqual("—", reversed.ElapsedText);

        var missing = new JobRowViewModel(Snapshot(
            started: null,
            lastActivity: Started,
            status: ForgeJobStatus.Running));
        Assert.AreEqual("—", missing.ElapsedText);
    }

    [DataTestMethod]
    [DataRow("job_mw8vbgms_9e9j", "…9e9j")]
    [DataRow("12345", "…2345")]
    [DataRow("1234", "1234")]
    [DataRow("abc", "abc")]
    [DataRow("aa😀123", "…😀123")]
    public void Job_DisplayUsesLastFourWithEllipsisWhenLong(
        string jobId,
        string expectedDisplay)
    {
        var row = new JobRowViewModel(Snapshot(jobId: jobId));

        Assert.AreEqual(expectedDisplay, row.JobDisplayText);
        Assert.AreEqual(jobId, row.JobId);
        Assert.AreEqual(jobId, row.Snapshot.Metadata.JobId);
    }

    [TestMethod]
    public void ReportedStatus_ReportedValueIsDisplayedWithUnverifiedTooltip()
    {
        var extraction = Extract("Status: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, extraction.State);

        Assert.AreEqual("DONE", ReportedStatusPresentation.ToDisplay(extraction));
        var tooltip = ReportedStatusPresentation.ToTooltip(extraction);
        StringAssert.StartsWith(tooltip, "DONE");
        StringAssert.Contains(tooltip, "unverified agent-reported");
        StringAssert.Contains(tooltip, "not authoritative");
        Assert.IsFalse(tooltip.Contains("deferred", StringComparison.OrdinalIgnoreCase));

        var row = new JobRowViewModel(Snapshot(reportedStatus: extraction));
        Assert.AreEqual("DONE", row.ReportedStatusText);
        Assert.AreEqual(tooltip, row.ReportedStatusTooltip);
    }

    [TestMethod]
    public void ReportedStatus_StatesWithoutAnUnambiguousValueRenderNeutralNA()
    {
        var notReported = Extract("Plan complete. No declaration here.");
        Assert.AreEqual(ReportedStatusState.NotReported, notReported.State);

        var empty = Extract("Status:\n");
        Assert.AreEqual(ReportedStatusState.Empty, empty.State);

        var ambiguous = Extract("Status: DONE\nStatus: FAILED");
        Assert.AreEqual(ReportedStatusState.Ambiguous, ambiguous.State);

        var unavailable = Extract("> Status: DONE");
        Assert.AreEqual(ReportedStatusState.Unavailable, unavailable.State);

        var ineligible = ReportedStatusParser.Extract(Incomplete());

        var emptyReportedValue = new ReportedStatusExtraction(
            ReportedStatusState.Reported,
            "   ",
            Array.Empty<ReportedStatusDeclaration>(),
            "Synthetic reported value without non-whitespace content.",
            Complete("Status: **   **", "Assistant-reported text"));

        var missing = (ReportedStatusExtraction?)null;

        foreach (var extraction in new ReportedStatusExtraction?[]
                 {
                     notReported, empty, ambiguous, unavailable, ineligible,
                     emptyReportedValue, missing
                 })
        {
            Assert.AreEqual("N/A", ReportedStatusPresentation.ToDisplay(extraction));
            var tooltip = ReportedStatusPresentation.ToTooltip(extraction);
            StringAssert.Contains(tooltip, "N/A");
            StringAssert.Contains(tooltip, "unverified");
        }
    }

    [TestMethod]
    public void ReportedStatus_RolelessValueIsDisplayedAsUnverified()
    {
        var roleless = ReportedStatusParser.Extract(
            Complete("Status: DONE", "Observed roleless text — unverified"));
        Assert.AreEqual(ReportedStatusState.Reported, roleless.State);
        Assert.IsFalse(roleless.IsRoleVerified);

        Assert.AreEqual("DONE", ReportedStatusPresentation.ToDisplay(roleless));
        StringAssert.Contains(
            ReportedStatusPresentation.ToTooltip(roleless),
            "unverified agent-reported");
    }

    [TestMethod]
    public void ReportedStatus_LongValueIsPreservedForWpfEllipsisAndFullTooltip()
    {
        const string longValue = "v1.2.3-complete-with-extra";
        var extraction = Extract("Status: **" + longValue + "**");
        Assert.AreEqual(longValue, extraction.Value);

        var display = ReportedStatusPresentation.ToDisplay(extraction);

        Assert.AreEqual(longValue, display);
        Assert.AreEqual(
            longValue + " — unverified agent-reported text; not authoritative Forge execution status.",
            ReportedStatusPresentation.ToTooltip(extraction));
    }

    [TestMethod]
    public void ReportedStatus_FifteenCharacters_RemainComplete()
    {
        const string exact = "v1.2.3-complete";
        Assert.AreEqual(15, exact.Length);
        var extraction = Extract("Status: **" + exact + "**");

        Assert.AreEqual(exact, ReportedStatusPresentation.ToDisplay(extraction));
        Assert.AreEqual(
            exact + " — unverified agent-reported text; not authoritative Forge execution status.",
            ReportedStatusPresentation.ToTooltip(extraction));
    }

    [TestMethod]
    public void ReportedStatus_UnicodeValueKeepsSurrogatePairsIntactWithoutTruncation()
    {
        const string emoji = "😀😀😀😀😀😀😀😀";
        Assert.AreEqual(16, emoji.Length);
        var extraction = Extract("Status: **" + emoji + "**");

        var display = ReportedStatusPresentation.ToDisplay(extraction);

        Assert.AreEqual(emoji, display);
        AssertSurrogatePairsIntact(display);
        StringAssert.StartsWith(ReportedStatusPresentation.ToTooltip(extraction), emoji);
    }

    [TestMethod]
    public void ReportedStatus_LongCombiningSequenceIsPreservedInFull()
    {
        var combining = "a" + new string('\u0301', 15);
        var extraction = Extract("Status: **" + combining + "**");
        Assert.AreEqual(ReportedStatusState.Reported, extraction.State);

        Assert.AreEqual(combining, ReportedStatusPresentation.ToDisplay(extraction));
    }

    [DataTestMethod]
    [DataRow(ForgeJobStatus.Unknown)]
    [DataRow(ForgeJobStatus.Done)]
    [DataRow(ForgeJobStatus.Failed)]
    public void ReportedStatus_RowDisplayDoesNotDependOnAuthoritativeForgeStatus(
        ForgeJobStatus authoritative)
    {
        var extraction = Extract("Status: reported");
        var snapshot = Snapshot(status: authoritative, reportedStatus: extraction);
        var row = new JobRowViewModel(snapshot);

        Assert.AreEqual("reported", row.ReportedStatusText);
        Assert.AreEqual(authoritative, row.Snapshot.Status);
        Assert.AreEqual(ForgeJobStatus.Unknown, snapshot.Metadata.ExplicitStatus);
    }

    [TestMethod]
    public void ReportedStatus_RowInitialAndRefreshUpdateBinding()
    {
        var row = new JobRowViewModel(Snapshot(reportedStatus: Extract("Status: DONE")));
        Assert.AreEqual("DONE", row.ReportedStatusText);
        StringAssert.StartsWith(row.ReportedStatusTooltip, "DONE");

        var changed = new List<string>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        row.Update(
            Snapshot(reportedStatus: Extract("Status: **FAILED**")),
            Started.AddMinutes(2));

        Assert.AreEqual("FAILED", row.ReportedStatusText);
        StringAssert.StartsWith(row.ReportedStatusTooltip, "FAILED");
        CollectionAssert.Contains(changed, nameof(JobRowViewModel.ReportedStatusText));
        CollectionAssert.Contains(changed, nameof(JobRowViewModel.ReportedStatusTooltip));

        row.Update(Snapshot(reportedStatus: null), Started.AddMinutes(3));
        Assert.AreEqual("N/A", row.ReportedStatusText);
        StringAssert.Contains(row.ReportedStatusTooltip, "N/A");
    }

    [TestMethod]
    public void ReportedStatusCell_UnavailableStatusUsesNormalizedJobActivity()
    {
        const string activityText = "Inspecting the bounded synthetic source and preparing the next step.";
        var row = new JobRowViewModel(Snapshot(
            reportedStatus: null,
            liveActivity: Activity(activityText)));

        Assert.AreEqual(
            "Inspecting the bounded synthetic source and preparing the next step",
            row.ReportedStatusCellText);
        StringAssert.StartsWith(
            row.ReportedStatusCellTooltip,
            "Inspecting the bounded synthetic source and preparing the next step");
        StringAssert.Contains(
            row.ReportedStatusCellTooltip,
            "unverified first-line activity text");
        StringAssert.Contains(row.ReportedStatusCellTooltip, "not authoritative");
    }

    [TestMethod]
    public void ReportedStatusCell_ReportedStatusWinsOverActivity()
    {
        var row = new JobRowViewModel(Snapshot(
            reportedStatus: Extract("Status: DONE"),
            liveActivity: Activity("Still working on another step.")));

        Assert.AreEqual("DONE", row.ReportedStatusCellText);
        Assert.AreEqual(
            LiveActivityStatusLinePolicy.Tooltip("DONE"),
            row.ReportedStatusCellTooltip);
    }

    [TestMethod]
    public void ReportedStatusCell_MissingStatusAndActivityStaysNeutralNA()
    {
        var row = new JobRowViewModel(Snapshot(reportedStatus: null));

        Assert.AreEqual("N/A", row.ReportedStatusCellText);
        StringAssert.Contains(row.ReportedStatusCellTooltip, "N/A");
    }

    [TestMethod]
    public void ReportedStatusCell_RefreshPublishesFallbackChanges()
    {
        var row = new JobRowViewModel(Snapshot(reportedStatus: null));
        Assert.AreEqual("N/A", row.ReportedStatusCellText);
        StringAssert.Contains(row.ReportedStatusCellTooltip, "N/A");

        var changed = new List<string>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        row.Update(
            Snapshot(reportedStatus: null, liveActivity: Activity("new activity")),
            Started.AddMinutes(2));

        Assert.AreEqual("new activity", row.ReportedStatusCellText);
        CollectionAssert.Contains(changed, nameof(JobRowViewModel.ReportedStatusCellText));
        CollectionAssert.Contains(changed, nameof(JobRowViewModel.ReportedStatusCellTooltip));
    }

    private static void AssertSurrogatePairsIntact(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Assert.IsTrue(
                    index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    "A high surrogate must be followed by a low surrogate.");
                index++;
            }
            else
            {
                Assert.IsFalse(char.IsLowSurrogate(value[index]), "A low surrogate has no high surrogate.");
            }
        }
    }

    private static ReportedStatusExtraction Extract(string text) =>
        ReportedStatusParser.Extract(Complete(text, "Assistant-reported text"));

    private static FullReportResult Complete(string text, string kind) =>
        new(
            FullReportStatus.Complete,
            "Complete guarded capture of one text part.",
            text,
            "job_synthetic",
            "session_synthetic",
            "message_synthetic",
            "part_synthetic",
            @"C:\synthetic\runs\job.log",
            128,
            512,
            Started,
            Started.AddMinutes(1),
            kind);

    private static FullReportResult Incomplete() =>
        new(
            FullReportStatus.Incomplete,
            "N/A — the bounded window cannot prove the complete record.",
            null,
            "job_synthetic",
            "session_synthetic",
            "message_synthetic",
            "part_synthetic",
            @"C:\synthetic\runs\job.log",
            128,
            512,
            Started,
            Started.AddMinutes(1),
            "Assistant-reported text");

    private static LiveActivitySnapshot Activity(string text) =>
        new()
        {
            Doing = new ActivityExcerpt(
                text,
                "Assistant-reported text",
                "job_mw8vbgms_9e9j",
                "session_synthetic",
                "message_synthetic",
                "part_synthetic",
                @"C:\synthetic\runs\job.log",
                128,
                Started,
                Started.AddMinutes(1)),
            Availability = "Bounded synthetic activity."
        };

    private static ForgeJobSnapshot Snapshot(
        string jobId = "job_mw8vbgms_9e9j",
        string? directory = @"D:\Repositories\WPF\DSMonitor",
        DateTimeOffset? started = null,
        DateTimeOffset? lastActivity = null,
        ForgeJobStatus status = ForgeJobStatus.Done,
        ReportedStatusExtraction? reportedStatus = null,
        DateTimeOffset? observedAt = null,
        LiveActivitySnapshot? liveActivity = null) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = jobId,
                MetadataPath = @"C:\synthetic\runs\" + jobId + ".json",
                Directory = directory,
                Started = started
            },
            Status = status,
            LastActivity = lastActivity,
            ObservedAt = observedAt ?? Started.AddMinutes(1),
            ReportedStatus = reportedStatus,
            LiveActivity = liveActivity ?? new LiveActivitySnapshot()
        };
}
