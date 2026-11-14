using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Synthetic coverage for Phase 2 Activity History: exact Text/Tools/Steps
/// classification, independent filters, merged step pairing, and exact tool
/// status/timing. No real Forge or OpenCode data is used.
/// </summary>
[TestClass]
public sealed class Phase2ActivityHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
    private const string SourcePath = @"C:\synthetic\runs\job.log";
    private static readonly LedgerSourceIdentity Identity = new(11, 22, 33);
    private const string TextKind = "Assistant-reported text";
    private const string ToolKind = "Observed tool activity";
    private const string StepStartKind = "Observed step start";
    private const string StepFinishKind = "Observed step finish";

    // ---------------- exact tool evidence ----------------

    [TestMethod]
    public void Reader_ToolRowMapsExactNameStatusStartAndDuration()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var start = Now.AddMinutes(-3).ToUnixTimeMilliseconds();
        var end = start + 4_000;
        workspace.WriteLog(
            "job",
            ToolRecord(Now.AddMinutes(-2).ToUnixTimeMilliseconds(), "shell", "completed", start, end));

        var activity = Read(workspace, Prepare(workspace));
        var row = activity.History.Single();

        Assert.AreEqual(LiveActivityKind.Tool, row.ActivityKind);
        Assert.AreEqual("shell", row.ToolName);
        Assert.AreEqual("completed", row.ToolStatus);
        Assert.AreEqual(LiveActivityToolOutcome.Completed, row.ToolOutcome);
        Assert.AreEqual(start, row.ToolStartUnixMilliseconds);
        Assert.AreEqual(TimeSpan.FromSeconds(4), row.ObservedDuration);
        Assert.AreEqual(start, row.DurationStartUnixMilliseconds);
        Assert.AreEqual(end, row.DurationEndUnixMilliseconds);

        var view = new LiveActivityHistoryRowViewModel(row, null, false);
        Assert.IsTrue(view.IsToolActivity);
        Assert.IsTrue(view.IsSuccessfulToolActivity);
        Assert.IsFalse(view.IsErrorToolActivity);
        Assert.AreEqual("shell · completed", view.ReportedStatusText);
        Assert.AreEqual(TimeSpan.FromSeconds(4).TotalSeconds, view.DurationSortSeconds);
        StringAssert.Contains(view.StartedTooltip, "state.time.start");
        StringAssert.Contains(view.Provenance, "exact state.status completed");
    }

    [TestMethod]
    public void Reader_ToolStatusIsExactAndNeverDerivedFromTheToolName()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-1).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            // A tool name containing success/error words must not affect the
            // outcome classification.
            ToolRecord(timestamp, "error-completer", "running", null, null, part: "p-running"),
            ToolRecord(timestamp + 1, "done-marker", "error", null, null, part: "p-error"),
            // Only the exact tokens completed/error are counted; equivalent
            // words keep their display value and are never a badge.
            ToolRecord(timestamp + 2, "shell", "complete", null, null, part: "p-complete"),
            ToolRecord(timestamp + 3, "shell", "done", null, null, part: "p-done"),
            ToolRecord(timestamp + 4, "shell", "failed", null, null, part: "p-failed-word"),
            ToolRecord(timestamp + 5, "shell", "failure", null, null, part: "p-failure-word"),
            ToolRecord(timestamp + 6, "shell", "pending", null, null, part: "p-pending"),
            ToolRecord(timestamp + 7, "shell", "queued", null, null, part: "p-unknown"),
            ToolRecord(timestamp + 8, "shell", null, null, null, part: "p-missing"),
            // Misleading names in both directions.
            ToolRecord(timestamp + 9, "error-checker", "completed", null, null, part: "p-name-error-completed"),
            ToolRecord(timestamp + 10, "completed", "complete", null, null, part: "p-name-completed"));

        var activity = Read(workspace, Prepare(workspace));
        var byPart = activity.History.ToDictionary(entry => entry.PartId, StringComparer.Ordinal);

        Assert.AreEqual(LiveActivityToolOutcome.Running, byPart["p-running"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Failed, byPart["p-error"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-complete"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-done"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-failed-word"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-failure-word"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Pending, byPart["p-pending"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-unknown"].ToolOutcome);
        Assert.AreEqual("queued", byPart["p-unknown"].ToolStatus);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-missing"].ToolOutcome);
        Assert.IsNull(byPart["p-missing"].ToolStatus);
        Assert.AreEqual(LiveActivityToolOutcome.Completed, byPart["p-name-error-completed"].ToolOutcome);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-name-completed"].ToolOutcome);

        var running = new LiveActivityHistoryRowViewModel(byPart["p-running"], null, false);
        var failed = new LiveActivityHistoryRowViewModel(byPart["p-error"], null, false);
        var complete = new LiveActivityHistoryRowViewModel(byPart["p-complete"], null, false);
        var done = new LiveActivityHistoryRowViewModel(byPart["p-done"], null, false);
        var failedWord = new LiveActivityHistoryRowViewModel(byPart["p-failed-word"], null, false);
        var failureWord = new LiveActivityHistoryRowViewModel(byPart["p-failure-word"], null, false);
        var pending = new LiveActivityHistoryRowViewModel(byPart["p-pending"], null, false);
        var nameErrorCompleted = new LiveActivityHistoryRowViewModel(
            byPart["p-name-error-completed"], null, false);
        var nameCompleted = new LiveActivityHistoryRowViewModel(byPart["p-name-completed"], null, false);

        // Exact outcome only: the tool name never contributes a badge, and
        // equivalent-looking status tokens are never counted as a badge.
        Assert.IsFalse(running.IsSuccessfulToolActivity);
        Assert.IsFalse(running.IsErrorToolActivity);
        Assert.IsTrue(failed.IsErrorToolActivity);
        Assert.IsFalse(failed.IsSuccessfulToolActivity);
        Assert.IsFalse(complete.IsSuccessfulToolActivity);
        Assert.IsFalse(complete.IsErrorToolActivity);
        Assert.IsFalse(done.IsSuccessfulToolActivity);
        Assert.IsFalse(done.IsErrorToolActivity);
        Assert.IsFalse(failedWord.IsSuccessfulToolActivity);
        Assert.IsFalse(failedWord.IsErrorToolActivity);
        Assert.IsFalse(failureWord.IsSuccessfulToolActivity);
        Assert.IsFalse(failureWord.IsErrorToolActivity);
        Assert.IsFalse(pending.IsSuccessfulToolActivity);
        Assert.IsFalse(pending.IsErrorToolActivity);
        Assert.IsTrue(nameErrorCompleted.IsSuccessfulToolActivity);
        Assert.IsFalse(nameErrorCompleted.IsErrorToolActivity);
        Assert.IsFalse(nameCompleted.IsSuccessfulToolActivity);
        Assert.IsFalse(nameCompleted.IsErrorToolActivity);

        // Display still preserves the exact source token.
        Assert.AreEqual("error-completer · running", running.ReportedStatusText);
        Assert.AreEqual("done-marker · error", failed.ReportedStatusText);
        Assert.AreEqual("shell · complete", complete.ReportedStatusText);
        Assert.AreEqual("shell · done", done.ReportedStatusText);
        Assert.AreEqual("shell · failed", failedWord.ReportedStatusText);
        Assert.AreEqual("shell · failure", failureWord.ReportedStatusText);
        Assert.AreEqual("shell · pending", pending.ReportedStatusText);
        Assert.AreEqual("error-checker · completed", nameErrorCompleted.ReportedStatusText);
        Assert.AreEqual("completed · complete", nameCompleted.ReportedStatusText);
    }

    [TestMethod]
    public void Reader_ToolTimeIsValidatedIndependentlyAndReversalsYieldNoDuration()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var start = Now.AddMinutes(-4).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            // Valid start, reversed end: Started keeps the validated start,
            // duration is N/A.
            ToolRecord(timestamp, "shell", "completed", start, start - 1_000, part: "p-reversed"),
            // Negative start: nothing usable.
            ToolRecord(timestamp + 1, "shell", "completed", -5, start, part: "p-negative"),
            // String-typed timing values are not integers.
            RawToolRecord(timestamp + 2, "shell", "completed", "\"1800000000000\"", "\"1800000001000\"", "p-string"),
            // Duplicate start keys are rejected rather than resolved last-wins.
            RawToolRecord(timestamp + 3, "shell", "completed", "1800000000000, \"start\": 1800000002000", "1800000003000", "p-duplicate"));

        var activity = Read(workspace, Prepare(workspace));
        var byPart = activity.History.ToDictionary(entry => entry.PartId, StringComparer.Ordinal);

        Assert.IsNull(byPart["p-reversed"].ObservedDuration);
        Assert.AreEqual(start, byPart["p-reversed"].ToolStartUnixMilliseconds);
        Assert.IsNull(byPart["p-reversed"].DurationStartUnixMilliseconds);
        Assert.IsNull(byPart["p-negative"].ToolStartUnixMilliseconds);
        Assert.IsNull(byPart["p-string"].ToolStartUnixMilliseconds);
        Assert.IsNull(byPart["p-string"].ObservedDuration);
        Assert.IsNull(byPart["p-duplicate"].ToolStartUnixMilliseconds);
        Assert.IsNull(byPart["p-duplicate"].ObservedDuration);

        var reversed = new LiveActivityHistoryRowViewModel(byPart["p-reversed"], null, false);
        var negative = new LiveActivityHistoryRowViewModel(byPart["p-negative"], null, false);
        Assert.AreEqual("N/A", reversed.DurationText);
        Assert.AreEqual(double.PositiveInfinity, reversed.DurationSortSeconds);
        Assert.AreEqual("N/A", negative.DurationText);
        Assert.IsTrue(reversed.IsSuccessfulToolActivity);

        // Started falls back to the source timestamp only when no validated
        // tool start exists.
        var sourceStart = byPart["p-negative"].SourceTimestamp!.Value;
        Assert.AreEqual(
            sourceStart.ToLocalTime().ToString("MMM d HH:mm:ss.fff", CultureInfo.InvariantCulture),
            negative.StartedText);
    }

    [TestMethod]
    public void Reader_ToolIdentity_IsVersionCompatibleAndKeepsCallIdentityDistinct()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        var start = Now.AddMinutes(-4).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            // OpenCode 1.x: id is the part identity; callID stays distinct.
            ToolRecord(
                timestamp,
                "shell",
                "completed",
                start,
                start + 1_000,
                part: "p-v1",
                callId: "call-v1"),
            // OpenCode 2.x: partID is the part identity; id is the call identity.
            ToolRecordV2(
                timestamp + 1,
                "edit",
                "error",
                null,
                null,
                part: "p-v2",
                callId: "call-v2"),
            // A missing call identifier remains compatible.
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 2) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{" +
            "\"partID\":\"p-v2-no-call\",\"messageID\":\"msg\",\"type\":\"tool\"," +
            "\"tool\":\"write\"}}");

        var activity = Read(workspace, Prepare(workspace));
        var byPart = activity.History.ToDictionary(entry => entry.PartId, StringComparer.Ordinal);

        Assert.AreEqual(3, activity.History.Count);
        Assert.AreEqual(3, activity.HistoryRecords.Count);
        Assert.AreEqual(3, byPart.Count);
        Assert.IsTrue(byPart.ContainsKey("p-v1"));
        Assert.IsTrue(byPart.ContainsKey("p-v2"));
        Assert.IsTrue(byPart.ContainsKey("p-v2-no-call"));
        Assert.IsFalse(byPart.ContainsKey("call-v1"));
        Assert.IsFalse(byPart.ContainsKey("call-v2"));

        Assert.AreEqual("ses", byPart["p-v1"].SessionId);
        Assert.AreEqual("msg", byPart["p-v1"].MessageId);
        Assert.AreEqual("shell", byPart["p-v1"].ToolName);
        Assert.AreEqual(LiveActivityToolOutcome.Completed, byPart["p-v1"].ToolOutcome);
        Assert.AreEqual("ses", byPart["p-v2"].SessionId);
        Assert.AreEqual("msg", byPart["p-v2"].MessageId);
        Assert.AreEqual("edit", byPart["p-v2"].ToolName);
        Assert.AreEqual(LiveActivityToolOutcome.Failed, byPart["p-v2"].ToolOutcome);
        Assert.AreEqual("write", byPart["p-v2-no-call"].ToolName);
        Assert.AreEqual(LiveActivityToolOutcome.Unknown, byPart["p-v2-no-call"].ToolOutcome);
    }

    [TestMethod]
    public void Reader_ToolIdentity_DuplicateOrMalformedPartEvidenceFailsClosed()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            // Duplicate partID keys.
            RawToolRecordV2(
                timestamp,
                "\"partID\":\"p-dup-1\",\"partID\":\"p-dup-2\""),
            // Duplicate V2 call identity keys.
            RawToolRecordV2(
                timestamp + 1,
                "\"partID\":\"p-dup-call\"",
                "\"id\":\"call-1\",\"id\":\"call-2\""),
            // A present but non-string partID must not fall back to id.
            RawToolRecordV2(
                timestamp + 2,
                "\"partID\":42",
                "\"id\":\"call-fallback\""),
            // Duplicate legacy id is rejected by the existing V1 alias gate.
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 3) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-1\"," +
            "\"id\":\"p-v1-2\",\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"shell\"}}",
            // Duplicate and non-string V1 callID evidence.
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 4) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-call\"," +
            "\"callID\":\"call-1\",\"callID\":\"call-2\",\"messageID\":\"msg\"," +
            "\"type\":\"tool\",\"tool\":\"shell\"}}",
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 5) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-call-type\"," +
            "\"callID\":42,\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"shell\"}}",
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 6) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-call-null\"," +
            "\"callID\":null,\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"shell\"}}",
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 7) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-call-empty\"," +
            "\"callID\":\"\",\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"shell\"}}",
            // Conflicting V2 id/callID evidence.
            RawToolRecordV2(
                timestamp + 8,
                "\"partID\":\"p-call-conflict\"",
                "\"id\":\"call-a\",\"callID\":\"call-b\""),
            // Explicitly null, empty, non-string, and invalid V2 id.
            RawToolRecordV2(
                timestamp + 9,
                "\"partID\":\"p-null-call\"",
                "\"id\":null"),
            RawToolRecordV2(
                timestamp + 10,
                "\"partID\":\"p-empty-call\"",
                "\"id\":\"\""),
            RawToolRecordV2(
                timestamp + 11,
                "\"partID\":\"p-number-call\"",
                "\"id\":42"),
            RawToolRecordV2(
                timestamp + 12,
                "\"partID\":\"p-invalid-call\"",
                "\"id\":\"bad\\u0001\""),
            // Invalid control characters in a V1 callID are malformed evidence.
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 13) +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"p-v1-call-invalid\"," +
            "\"callID\":\"bad\\u0001\",\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"shell\"}}");

        var activity = Read(workspace, Prepare(workspace));

        Assert.AreEqual(0, activity.History.Count);
        Assert.AreEqual(0, activity.HistoryRecords.Count);
        Assert.IsNull(activity.Doing);
    }

    [TestMethod]
    public void Reader_ToolIdentity_MissingOrUnsafePartIdIsPreviewOnly()
    {
        using var v2Workspace = new TestWorkspace();
        v2Workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        v2Workspace.WriteLog(
            "job",
            RawToolRecordV2(
                timestamp,
                "\"partID\":null",
                "\"id\":\"call-present\"",
                message: "msg"),
            RawToolRecordV2(
                timestamp + 1,
                "\"partID\":\"bad\\u0001\"",
                "\"id\":\"call-present-unsafe\"",
                message: "msg"));
        var v2 = Read(v2Workspace, Prepare(v2Workspace));
        Assert.IsNotNull(v2.Doing);
        Assert.AreEqual("ses", v2.Doing.SessionId);
        Assert.IsNull(v2.Doing.PartId);
        Assert.AreEqual(0, v2.History.Count);
        Assert.AreEqual(0, v2.HistoryRecords.Count);

        using var v1Workspace = new TestWorkspace();
        v1Workspace.WriteMetadata("job");
        v1Workspace.WriteLog(
            "job",
            "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{" +
            "\"callID\":\"call-present\",\"messageID\":\"msg\",\"type\":\"tool\"," +
            "\"tool\":\"shell\"}}");
        var v1 = Read(v1Workspace, Prepare(v1Workspace));
        Assert.IsNotNull(v1.Doing);
        Assert.AreEqual("ses", v1.Doing.SessionId);
        Assert.IsNull(v1.Doing.PartId);
        Assert.AreEqual(0, v1.History.Count);
        Assert.AreEqual(0, v1.HistoryRecords.Count);
    }

    [TestMethod]
    public void Reader_ToolIdentity_RejectsSessionAndMessageConflicts()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            // Root/part session conflict.
            "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"call-1\"," +
            "\"partID\":\"p-session-conflict\",\"sessionID\":\"other\",\"messageID\":\"msg\"," +
            "\"type\":\"tool\",\"tool\":\"shell\"}}",
            // Root/part message conflict.
            "{\"type\":\"tool_use\",\"timestamp\":" + (timestamp + 1) +
            ",\"sessionID\":\"ses\",\"messageID\":\"msg\",\"role\":\"assistant\"," +
            "\"part\":{\"id\":\"call-2\",\"partID\":\"p-message-conflict\"," +
            "\"messageID\":\"other\",\"type\":\"tool\",\"tool\":\"shell\"}}");

        var activity = Read(workspace, Prepare(workspace));

        Assert.AreEqual(0, activity.History.Count);
        Assert.AreEqual(0, activity.HistoryRecords.Count);
        Assert.IsNull(activity.Doing);
    }

    [TestMethod]
    public void Reader_ToolIdentity_KeepsJobsSessionsAndMessagesDistinctForSharedPartId()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("jobA");
        workspace.WriteMetadata("jobB");
        var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "jobA",
            ToolRecordV2(
                timestamp,
                "shell",
                "completed",
                null,
                null,
                session: "sesA",
                part: "shared-part",
                message: "msgA",
                callId: "callA"));
        workspace.WriteLog(
            "jobB",
            ToolRecordV2(
                timestamp,
                "edit",
                "error",
                null,
                null,
                session: "sesB",
                part: "shared-part",
                message: "msgB",
                callId: "callB"));

        var jobA = Read(workspace, Prepare(workspace, "jobA"));
        var jobB = Read(workspace, Prepare(workspace, "jobB"));

        var a = jobA.History.Single();
        Assert.AreEqual("jobA", a.JobId);
        Assert.AreEqual("sesA", a.SessionId);
        Assert.AreEqual("msgA", a.MessageId);
        Assert.AreEqual("shared-part", a.PartId);
        var b = jobB.History.Single();
        Assert.AreEqual("jobB", b.JobId);
        Assert.AreEqual("sesB", b.SessionId);
        Assert.AreEqual("msgB", b.MessageId);
        Assert.AreEqual("shared-part", b.PartId);
    }

    // ---------------- step records and pairing ----------------

    [TestMethod]
    public void Reader_RetainsExactStepRecordsAsTheirOwnIdentityCheckedEntries()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var t1 = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var t2 = Now.AddMinutes(-4).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            "{\"type\":\"step_start\",\"timestamp\":" + t1 +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"step-start-1\"," +
            "\"messageID\":\"msg\",\"type\":\"step-start\"}}",
            "{\"type\":\"step_finish\",\"timestamp\":" + t2 +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"step-finish-1\"," +
            "\"messageID\":\"msg\",\"type\":\"step-finish\",\"reason\":\"tool-calls\"}}");

        var activity = Read(workspace, Prepare(workspace));
        var start = activity.History.Single(entry => entry.PartId == "step-start-1");
        var finish = activity.History.Single(entry => entry.PartId == "step-finish-1");

        Assert.AreEqual(LiveActivityKind.StepStart, start.ActivityKind);
        Assert.AreEqual(LiveActivityKind.StepFinish, finish.ActivityKind);
        Assert.AreEqual("tool-calls", finish.StepFinishReason);
        Assert.IsNull(start.StepFinishReason);
        Assert.IsNull(start.ObservedDuration);
        Assert.IsNull(finish.ObservedDuration);
        Assert.AreNotEqual(start.ByteOffset, finish.ByteOffset);
        Assert.AreNotEqual(start.RecordDigest, finish.RecordDigest);
        Assert.AreEqual(2, activity.HistoryRecords.Count);
        Assert.IsTrue(activity.HistoryRecords.All(entry => !entry.StepIdentityConflict));

        // Step records never leak into the preview or the terminal evidence.
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
        Assert.IsNull(activity.DoneEventByteOffset);
        Assert.AreEqual(finish.ByteOffset, activity.LatestLifecycleByteOffset);
    }

    [TestMethod]
    public void Reader_DuplicateStepIdentityIsFlaggedBeforeNewestPerPartDedup()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var t1 = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        workspace.WriteLog(
            "job",
            "{\"type\":\"step_start\",\"timestamp\":" + t1 +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"shared-step\"," +
            "\"messageID\":\"msg\",\"type\":\"step-start\"}}",
            // Same exact identity and timestamp, different raw bytes: the
            // ledger admits a same-timestamp reread, and the reader must still
            // flag the duplicate step identity before de-duplication.
            "{\"type\":\"step_start\",\"timestamp\":" + t1 +
            ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"shared-step\"," +
            "\"messageID\":\"msg\",\"type\":\"step-start\",\"revision\":\"b\"}}");

        var activity = Read(workspace, Prepare(workspace));

        // One retained row (newest record), but the conflict evidence survives
        // de-duplication on both the retained entry and the pre-dedup records.
        Assert.AreEqual(1, activity.History.Count);
        Assert.IsTrue(activity.History.Single().StepIdentityConflict);
        Assert.AreEqual(2, activity.HistoryRecords.Count);
        Assert.IsTrue(activity.HistoryRecords.All(entry => entry.StepIdentityConflict));

        var projected = LiveActivityHistoryProjection.Merge(activity.History);
        Assert.AreEqual(1, projected.Count);
        Assert.AreEqual(LiveActivityStepState.Ambiguous, projected[0].Step!.State);
    }

    [TestMethod]
    public void Projection_MergesEverySequentialPairInSourceOrder()
    {
        var entries = new[]
        {
            Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-6)),
            Step("job", "ses", "msg", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-5), "tool-calls"),
            Step("job", "ses", "msg", "s2", LiveActivityKind.StepStart, 300, Now.AddMinutes(-4)),
            Step("job", "ses", "msg", "f2", LiveActivityKind.StepFinish, 400, Now.AddMinutes(-3), "stop"),
            Step("job", "ses", "msg", "s3", LiveActivityKind.StepStart, 500, Now.AddMinutes(-2))
        };

        var projected = LiveActivityHistoryProjection.Merge(entries);
        var steps = projected.Where(item => item.Step is not null).ToArray();

        Assert.AreEqual(3, steps.Length);
        // Newest first, and each merged row keeps the start record identity.
        Assert.AreEqual("s3", steps[0].Entry.PartId);
        Assert.AreEqual(LiveActivityStepState.UnmatchedStart, steps[0].Step!.State);
        Assert.AreEqual("s2", steps[1].Entry.PartId);
        Assert.AreEqual(LiveActivityStepState.Paired, steps[1].Step!.State);
        Assert.AreEqual("f2", steps[1].Step!.Finish!.PartId);
        Assert.AreEqual("s1", steps[2].Entry.PartId);
        Assert.AreEqual(LiveActivityStepState.Paired, steps[2].Step!.State);
        Assert.AreEqual("f1", steps[2].Step!.Finish!.PartId);

        // Paired rows render the start timestamp, the finish reason, and the
        // exact two-timestamp span; both records stay in provenance.
        var first = new LiveActivityHistoryRowViewModel(
            steps[2].Entry,
            null,
            false,
            null,
            null,
            steps[2].Step,
            Now,
            false);
        Assert.AreEqual("Step: tool-calls", first.ReportedStatusText);
        Assert.AreEqual(60d, first.DurationSortSeconds, 1e-9);
        StringAssert.Contains(first.Provenance, "part f1");
        StringAssert.Contains(first.Provenance, "part s1");
        Assert.IsFalse(first.IsTextActivity);
        Assert.IsFalse(first.IsToolActivity);
        Assert.IsTrue(first.IsStepActivity);
    }

    [TestMethod]
    public void Projection_RepeatedStartsMakeTheRunAmbiguousAndNeverPairTheFinish()
    {
        var entries = new[]
        {
            Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-3)),
            Step("job", "ses", "msg", "s2", LiveActivityKind.StepStart, 200, Now.AddMinutes(-2)),
            Step("job", "ses", "msg", "f1", LiveActivityKind.StepFinish, 300, Now.AddMinutes(-1), "stop")
        };

        var steps = LiveActivityHistoryProjection.Merge(entries)
            .Where(item => item.Step is not null)
            .ToArray();

        Assert.AreEqual(3, steps.Length);
        Assert.AreEqual(2, steps.Count(item => item.Step!.State == LiveActivityStepState.Ambiguous));
        Assert.AreEqual(1, steps.Count(item => item.Step!.State == LiveActivityStepState.UnmatchedFinish));
        Assert.AreEqual(0, steps.Count(item => item.Step!.State == LiveActivityStepState.Paired));

        var finish = steps.Single(item => item.Entry.PartId == "f1");
        var row = new LiveActivityHistoryRowViewModel(
            finish.Entry, null, false, null, null, finish.Step, Now, false);
        Assert.AreEqual("Step: unmatched finish", row.ReportedStatusText);
        Assert.AreEqual("N/A", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "no visible start");
    }

    [TestMethod]
    public void Projection_OrphansUnfinishedAndConflictsFailClosed()
    {
        var entries = new[]
        {
            // Orphan finish.
            Step("job", "ses", "msg", "f-only", LiveActivityKind.StepFinish, 100, Now.AddMinutes(-3), "end-turn"),
            // Unterminated start without defensible active evidence.
            Step("job", "ses", "msgB", "s-only", LiveActivityKind.StepStart, 200, Now.AddMinutes(-2)),
            // Conflicting duplicate identity supplied by the reader flag.
            Step("job", "ses", "msgC", "dup", LiveActivityKind.StepStart, 300, Now.AddMinutes(-1), conflict: true)
        };

        var steps = LiveActivityHistoryProjection.Merge(entries)
            .Where(item => item.Step is not null)
            .ToArray();

        Assert.AreEqual(3, steps.Length);
        Assert.AreEqual(
            LiveActivityStepState.UnmatchedFinish,
            steps.Single(item => item.Entry.PartId == "f-only").Step!.State);
        var orphanFinishEntry = steps.Single(item => item.Entry.PartId == "f-only");
        var orphanFinishRow = new LiveActivityHistoryRowViewModel(
            orphanFinishEntry.Entry,
            null,
            false,
            null,
            null,
            orphanFinishEntry.Step,
            Now,
            isActiveStep: true);
        Assert.AreEqual("Step: unmatched finish", orphanFinishRow.ReportedStatusText);
        Assert.AreEqual(
            LiveActivityStepState.UnmatchedStart,
            steps.Single(item => item.Entry.PartId == "s-only").Step!.State);
        Assert.AreEqual(
            LiveActivityStepState.Ambiguous,
            steps.Single(item => item.Entry.PartId == "dup").Step!.State);

        var startRow = new LiveActivityHistoryRowViewModel(
            steps.Single(item => item.Entry.PartId == "s-only").Entry,
            null,
            false,
            null,
            null,
            steps.Single(item => item.Entry.PartId == "s-only").Step,
            Now,
            isActiveStep: false);
        Assert.AreEqual("Step: unfinished", startRow.ReportedStatusText);
        Assert.AreEqual("N/A", startRow.DurationText);
        StringAssert.Contains(startRow.DurationTooltip, "no visible finish");

        var ambiguousRow = new LiveActivityHistoryRowViewModel(
            steps.Single(item => item.Entry.PartId == "dup").Entry,
            null,
            false,
            null,
            null,
            steps.Single(item => item.Entry.PartId == "dup").Step,
            Now,
            false);
        Assert.AreEqual("Step: ambiguous", ambiguousRow.ReportedStatusText);
        Assert.AreEqual("N/A", ambiguousRow.DurationText);
    }

    [TestMethod]
    public void Projection_NeverPairsAcrossJobsSessionsMessagesOrDistinctOffsets()
    {
        var entries = new[]
        {
            Step("job", "ses", "msgA", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-5)),
            Step("job", "ses", "msgB", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-4), "stop"),
            Step("job", "other-ses", "msgA", "s2", LiveActivityKind.StepStart, 300, Now.AddMinutes(-3)),
            Step("other-job", "ses", "msgA", "f2", LiveActivityKind.StepFinish, 400, Now.AddMinutes(-2), "stop"),
            // Two records claiming one offset cannot prove continuity.
            Step("job", "ses", "msgC", "s3", LiveActivityKind.StepStart, 500, Now.AddMinutes(-1)),
            Step("job", "ses", "msgC", "f3", LiveActivityKind.StepFinish, 500, Now.AddMinutes(-1), "stop", digest: "other")
        };

        var steps = LiveActivityHistoryProjection.Merge(entries)
            .Where(item => item.Step is not null)
            .ToArray();

        Assert.AreEqual(6, steps.Length);
        Assert.AreEqual(0, steps.Count(item => item.Step!.State == LiveActivityStepState.Paired));
        Assert.AreEqual(2, steps.Count(item => item.Step!.State == LiveActivityStepState.UnmatchedStart));
        Assert.AreEqual(2, steps.Count(item => item.Step!.State == LiveActivityStepState.UnmatchedFinish));
        Assert.AreEqual(2, steps.Count(item => item.Step!.State == LiveActivityStepState.Ambiguous));
    }

    [TestMethod]
    public void Projection_LeavesNonStepRowsUnchanged()
    {
        var entries = new[]
        {
            Entry("job", "ses", "msg", "t1", LiveActivityKind.Text, 10, Now.AddMinutes(-2), TextKind, "text"),
            Entry("job", "ses", "msg", "tool1", LiveActivityKind.Tool, 20, Now.AddMinutes(-1), ToolKind, "tool")
        };

        var projected = LiveActivityHistoryProjection.Merge(entries);

        Assert.AreEqual(2, projected.Count);
        Assert.IsTrue(projected.All(item => item.Step is null));
        CollectionAssert.AreEqual(
            new[] { "tool1", "t1" },
            projected.Select(item => item.Entry.PartId).ToArray());
    }

    [TestMethod]
    public void Row_StepSpanUsesOnlyValidOrderedSourceTimestamps()
    {
        var start = Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-4));
        var finish = Step("job", "ses", "msg", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-1), "stop");
        var pair = new LiveActivityStepPair(start, finish, LiveActivityStepState.Paired);

        var valid = new LiveActivityHistoryRowViewModel(start, null, false, null, null, pair, Now, false);
        Assert.AreEqual(TimeSpan.FromMinutes(3).TotalSeconds, valid.DurationSortSeconds);
        Assert.AreEqual("3m 0.000s", valid.DurationText);
        StringAssert.Contains(valid.DurationTooltip, "Step source span");
        Assert.AreEqual(
            start.SourceTimestamp!.Value.ToLocalTime().ToString(
                "MMM d HH:mm:ss.fff",
                CultureInfo.InvariantCulture),
            valid.StartedText);

        // Reversed timestamps: paired, but no invented duration.
        var reversedPair = new LiveActivityStepPair(start, finish with { SourceTimestamp = Now.AddMinutes(-30) }, LiveActivityStepState.Paired);
        var reversed = new LiveActivityHistoryRowViewModel(start, null, false, null, null, reversedPair, Now, false);
        Assert.AreEqual("N/A", reversed.DurationText);
        StringAssert.Contains(reversed.DurationTooltip, "missing, unordered, or invalid");

        // Missing timestamps: paired, but no duration.
        var missingPair = new LiveActivityStepPair(
            start with { SourceTimestamp = null },
            finish with { SourceTimestamp = null },
            LiveActivityStepState.Paired);
        var missing = new LiveActivityHistoryRowViewModel(
            start with { SourceTimestamp = null }, null, false, null, null, missingPair, Now, false);
        Assert.AreEqual("N/A", missing.DurationText);
        Assert.AreEqual("N/A", missing.StartedText);
    }

    [TestMethod]
    public void Row_UnpairedStartIsLiveOnlyWithDefensibleActiveEvidence()
    {
        var start = Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-2));
        var state = LiveActivityStepPair.Single(start, LiveActivityStepState.UnmatchedStart);

        var active = new LiveActivityHistoryRowViewModel(start, null, false, null, null, state, Now, true);
        Assert.AreEqual(TimeSpan.FromMinutes(2).TotalSeconds, active.DurationSortSeconds);
        Assert.AreEqual("2m 0.000s", active.DurationText);
        StringAssert.Contains(active.DurationTooltip, "Live source span");
        Assert.AreEqual("Step: in progress", active.ReportedStatusText);

        // Termination (or any loss of defensible evidence) stops the live span.
        var inactive = new LiveActivityHistoryRowViewModel(start, null, false, null, null, state, Now, false);
        Assert.AreEqual("N/A", inactive.DurationText);
        StringAssert.Contains(inactive.DurationTooltip, "no defensible");
        Assert.AreEqual("Step: unfinished", inactive.ReportedStatusText);
    }

    [TestMethod]
    public void Row_StepStateLabelsAreExact()
    {
        var start = Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-4));
        var finish = Step("job", "ses", "msg", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-3), "stop");

        static string Label(LiveActivityStepPair pair, bool active) =>
            new LiveActivityHistoryRowViewModel(pair.Entry, null, false, null, null, pair, Now, active)
                .ReportedStatusText;

        Assert.AreEqual(
            "Step: stop",
            Label(new LiveActivityStepPair(start, finish, LiveActivityStepState.Paired), false));
        Assert.AreEqual(
            "Step: unmatched finish",
            Label(LiveActivityStepPair.Single(finish, LiveActivityStepState.UnmatchedFinish), true));
        Assert.AreEqual(
            "Step: unmatched finish",
            Label(LiveActivityStepPair.Single(finish, LiveActivityStepState.UnmatchedFinish), false));
        Assert.AreEqual(
            "Step: in progress",
            Label(LiveActivityStepPair.Single(start, LiveActivityStepState.UnmatchedStart), true));
        Assert.AreEqual(
            "Step: unfinished",
            Label(LiveActivityStepPair.Single(start, LiveActivityStepState.UnmatchedStart), false));
        Assert.AreEqual(
            "Step: ambiguous",
            Label(LiveActivityStepPair.Single(start, LiveActivityStepState.Ambiguous), true));

        // A paired row with no usable finish reason falls back to the approved
        // neutral finish label rather than borrowing unrelated text.
        var fallback = new LiveActivityStepPair(
            start,
            finish with { StepFinishReason = null },
            LiveActivityStepState.Paired);
        Assert.AreEqual("Step: finished", Label(fallback, false));
    }

    [TestMethod]
    public void Row_StepAndToolRowsNeverClaimTheJobReportedStatusOrAStepText()
    {
        var jobStatus = new ReportedStatusPresentation.JobReportedStatus("job", "DONE");
        var step = Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-2));
        var pair = LiveActivityStepPair.Single(step, LiveActivityStepState.Ambiguous);
        var stepRow = new LiveActivityHistoryRowViewModel(step, null, false, jobStatus, null, pair, Now, false);
        Assert.AreEqual("Step: ambiguous", stepRow.ReportedStatusText);

        var tool = Entry("job", "ses", "msg", "t1", LiveActivityKind.Tool, 10, Now.AddMinutes(-1), ToolKind, "tool") with
        {
            ToolName = "shell",
            ToolStatus = "completed",
            ToolOutcome = LiveActivityToolOutcome.Completed
        };
        var toolRow = new LiveActivityHistoryRowViewModel(tool, null, false, jobStatus);
        Assert.AreEqual("shell · completed", toolRow.ReportedStatusText);

        // The job-level fallback still applies to text rows.
        var text = Entry("job", "ses", "msg", "x1", LiveActivityKind.Text, 5, Now.AddMinutes(-3), TextKind, string.Empty);
        var textRow = new LiveActivityHistoryRowViewModel(text, null, false, jobStatus);
        Assert.AreEqual("DONE", textRow.ReportedStatusText);
    }

    // ---------------- bounded history and continuity ----------------

    [TestMethod]
    public void History_StepPairDisappearsWhenTheWindowEvictsItsStart()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var start = Step("job", "ses", "msg", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-4));
        var finish = Step("job", "ses", "msg", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-3), "stop");

        var seeded = buffer.Apply(
            SnapshotWith([start, finish], 0, 300),
            "job",
            SourcePath,
            Observation(captured: 300));
        Assert.AreEqual(1, LiveActivityHistoryProjection.Merge(seeded)
            .Count(item => item.Step?.State == LiveActivityStepState.Paired));

        // The window advances past the start record: the retained finish can no
        // longer be paired, and the pair is never resurrected.
        var advanced = buffer.Apply(
            SnapshotWith([finish], 150, 400),
            "job",
            SourcePath,
            Observation(captured: 400));
        var projected = LiveActivityHistoryProjection.Merge(advanced);
        Assert.AreEqual(1, projected.Count);
        Assert.AreEqual(LiveActivityStepState.UnmatchedFinish, projected[0].Step!.State);

        // A conflicting rewrite at the same offset clears the buffer instead of
        // fabricating continuity.
        var rewritten = buffer.Apply(
            SnapshotWith([finish with { RecordDigest = "CHANGED" }], 150, 400),
            "job",
            SourcePath,
            Observation(captured: 400));
        Assert.AreEqual(1, rewritten.Count);
        Assert.AreEqual("CHANGED", rewritten[0].RecordDigest);
    }

    [TestMethod]
    public void Reader_StepPairIsNotInferredWhenTheStartIsOutsideTheBoundedWindow()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var t1 = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var start = "{\"type\":\"step_start\",\"timestamp\":" + t1 +
                    ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"step-start-1\"," +
                    "\"messageID\":\"msg\",\"type\":\"step-start\"}}";
        var finish = "{\"type\":\"step_finish\",\"timestamp\":" + (t1 + 1_000) +
                     ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"step-finish-1\"," +
                     "\"messageID\":\"msg\",\"type\":\"step-finish\",\"reason\":\"stop\"}}";
        workspace.WriteLog("job", start, finish);

        var prepared = Prepare(workspace);
        var total = Encoding.UTF8.GetByteCount(start + "\n" + finish + "\n");

        var full = JobLiveActivityReader.Read(
            prepared.Job, prepared.Evidence, prepared.Observation, workspace.RunsRoot, Now);
        Assert.IsTrue(full.History.Any(entry => entry.PartId == "step-start-1"));
        Assert.IsTrue(full.History.Any(entry => entry.PartId == "step-finish-1"));
        Assert.AreEqual(
            1,
            LiveActivityHistoryProjection.Merge(full.History)
                .Count(item => item.Step?.State == LiveActivityStepState.Paired));

        // Starting ten bytes into the file truncates the start record, which is
        // discarded as a possibly partial record. The finish stays complete but
        // must never be paired with an invisible start.
        var windowed = JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            maxBytes: total - 10);
        Assert.IsFalse(windowed.History.Any(entry => entry.PartId == "step-start-1"));
        Assert.IsTrue(windowed.History.Any(entry => entry.PartId == "step-finish-1"));

        var projected = LiveActivityHistoryProjection.Merge(windowed.History);
        Assert.IsTrue(projected.All(item =>
            item.Step?.State != LiveActivityStepState.Paired));
        Assert.AreEqual(
            LiveActivityStepState.UnmatchedFinish,
            projected.Single().Step!.State);
    }

    // ---------------- selection and filters ----------------

    [TestMethod]
    public void JobRow_MergesStepPairAndHidesSelectionWhenTheFilterExcludesIt()
    {
        var textEntry = Entry("job", "ses", "msg1", "text-1", LiveActivityKind.Text, 10, Now.AddMinutes(-3), TextKind, "Status: DONE");
        var start = Step("job", "ses", "msg2", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-2));
        var finish = Step("job", "ses", "msg2", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-1), "stop");
        var row = new JobRowViewModel(Snapshot("job", textEntry, start, finish));

        Assert.AreEqual(2, row.LiveActivityHistory.Count);
        var stepRow = row.LiveActivityHistory.Single(item => item.IsStepActivity);
        Assert.AreEqual("s1", stepRow.Entry.PartId);

        // Newest row is the merged step; it is initially selected.
        Assert.AreSame(stepRow, row.SelectedActivity);

        // Filtering to text keeps the visible text row selected, without
        // requesting a selected-text read.
        var selectionChanges = 0;
        row.SelectedActivityChanged += (_, _) => selectionChanges++;
        var textRow = row.LiveActivityHistory.Single(item => item.IsTextActivity);
        row.SetActivityHistoryVisibility(1);
        row.EnsureSelectedActivityVisible([textRow]);

        Assert.AreEqual(0, selectionChanges);
        Assert.AreSame(textRow, row.SelectedActivity);
        Assert.IsTrue(row.HasVisibleActivityHistory);
        Assert.AreEqual(row.SelectedActivity!.Entry.Text, row.SelectedActivityText);

        // Nothing visible: the row is cleared so a hidden activity is never
        // exposed in the Response panel.
        row.SetActivityHistoryVisibility(0);
        row.EnsureSelectedActivityVisible(Array.Empty<LiveActivityHistoryRowViewModel>());
        Assert.IsNull(row.SelectedActivity);
        Assert.AreEqual("N/A", row.SelectedActivityText);
        Assert.AreEqual("No activity selected.", row.SelectedActivityCompletenessText);
        Assert.IsFalse(row.HasVisibleActivityHistory);
        Assert.AreEqual("No activity matches the selected filters.", row.ActivityHistoryEmptyText);
        Assert.AreEqual(0, selectionChanges);
    }

    [TestMethod]
    public void JobRow_StepRowsAreNotTextTargetsForFullReportRetrieval()
    {
        var start = Step("job", "ses", "msg2", "s1", LiveActivityKind.StepStart, 100, Now.AddMinutes(-2));
        var finish = Step("job", "ses", "msg2", "f1", LiveActivityKind.StepFinish, 200, Now.AddMinutes(-1), "stop");
        var tool = Entry("job", "ses", "msg3", "tool-1", LiveActivityKind.Tool, 300, Now, ToolKind, "tool") with
        {
            ToolName = "shell",
            ToolStatus = "completed",
            ToolOutcome = LiveActivityToolOutcome.Completed
        };
        var row = new JobRowViewModel(Snapshot("job", start, finish, tool));

        var stepRow = row.LiveActivityHistory.Single(item => item.IsStepActivity);
        row.SelectedActivity = stepRow;
        Assert.IsFalse(row.IsSelectedActivityTextKind);

        row.SelectedActivity = row.LiveActivityHistory.Single(item => item.IsToolActivity);
        Assert.IsFalse(row.IsSelectedActivityTextKind);
    }

    // ---------------- presentation wiring ----------------

    [STATestClass]
    public sealed class FilterTests
    {
        [TestMethod]
        public void Filters_AreIndependentExactCountedMayBeEmptyAndSortNewestStartedFirst()
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
                var jobA = Snapshot(
                    "jobA",
                    Entry("jobA", "ses", "m1", "text-1", LiveActivityKind.Text, 10, Now.AddMinutes(-5), TextKind, "first"),
                    Entry("jobA", "ses", "m2", "text-2", LiveActivityKind.Text, 20, Now.AddMinutes(-4), TextKind, "second"),
                    Tool("jobA", "ses", "m3", "tool-ok", 30, Now.AddMinutes(-3), "shell", "completed", LiveActivityToolOutcome.Completed),
                    Tool("jobA", "ses", "m4", "tool-bad", 40, Now.AddMinutes(-2), "shell", "error", LiveActivityToolOutcome.Failed),
                    Step("jobA", "ses", "m5", "s1", LiveActivityKind.StepStart, 50, Now.AddMinutes(-1)),
                    Step("jobA", "ses", "m5", "f1", LiveActivityKind.StepFinish, 60, Now, "stop"));
                var jobB = Snapshot(
                    "jobB",
                    Entry("jobB", "ses", "m1", "b-text", LiveActivityKind.Text, 10, Now, TextKind, "b"));

                viewModel.ApplySnapshot(new MonitoringSnapshot
                {
                    ObservedAt = Now,
                    State = MonitoringState.Monitoring,
                    Jobs = [jobA, jobB],
                    Warnings = Array.Empty<string>()
                });

                var rowA = viewModel.Jobs.Single(job => job.JobId == "jobA");
                viewModel.SelectedJob = rowA;

                // Exact counts over every eligible de-duplicated row.
                Assert.AreEqual("Text (✓2)", viewModel.ActivityTextFilterLabel);
                Assert.AreEqual("Tools (✓1 · ✕1)", viewModel.ActivityToolsFilterLabel);
                Assert.AreEqual("Steps (1)", viewModel.ActivityStepsFilterLabel);

                // Default: text only, and the newest visible text row is selected.
                Assert.IsTrue(viewModel.ShowActivityTextFilter);
                Assert.IsFalse(viewModel.ShowActivityToolsFilter);
                Assert.IsFalse(viewModel.ShowActivityStepsFilter);
                Assert.AreEqual(2, viewModel.FilteredActivityHistory.Count);
                Assert.IsTrue(viewModel.FilteredActivityHistory.All(row => row.IsTextActivity));
                Assert.IsTrue(rowA.SelectedActivity is { IsTextActivity: true });

                // Every category can be turned off, leaving an intentionally empty view.
                viewModel.ShowActivityTextFilter = false;
                Assert.IsFalse(viewModel.ShowActivityTextFilter);
                Assert.AreEqual(0, viewModel.FilteredActivityHistory.Count);
                Assert.IsFalse(rowA.HasVisibleActivityHistory);

                viewModel.ShowActivityTextFilter = true;
                Assert.AreEqual(2, viewModel.FilteredActivityHistory.Count);

                // Independent toggles.
                viewModel.ShowActivityToolsFilter = true;
                Assert.AreEqual(4, viewModel.FilteredActivityHistory.Count);
                CollectionAssert.AreEqual(
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .OrderByDescending(value => value)
                        .ToArray(),
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .ToArray());
                viewModel.ShowActivityTextFilter = false;
                Assert.IsFalse(viewModel.ShowActivityTextFilter);
                Assert.AreEqual(2, viewModel.FilteredActivityHistory.Count);
                Assert.IsTrue(viewModel.FilteredActivityHistory.All(row => row.IsToolActivity));

                // All filters can be cleared, including after selecting tools.
                viewModel.ShowActivityToolsFilter = false;
                Assert.IsFalse(viewModel.ShowActivityToolsFilter);
                Assert.AreEqual(0, viewModel.FilteredActivityHistory.Count);

                viewModel.ShowActivityStepsFilter = true;
                Assert.AreEqual(1, viewModel.FilteredActivityHistory.Count);
                viewModel.ShowActivityTextFilter = true;
                viewModel.ShowActivityToolsFilter = true;
                Assert.AreEqual(5, viewModel.FilteredActivityHistory.Count);
                CollectionAssert.AreEqual(
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .OrderByDescending(value => value)
                        .ToArray(),
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .ToArray());
                viewModel.ShowActivityToolsFilter = false;
                Assert.AreEqual(3, viewModel.FilteredActivityHistory.Count);
                Assert.AreEqual(
                    2,
                    viewModel.FilteredActivityHistory.Count(row => row.IsTextActivity));
                Assert.AreEqual(
                    1,
                    viewModel.FilteredActivityHistory.Count(row => row.IsStepActivity));
                Assert.IsFalse(viewModel.FilteredActivityHistory.Any(row => row.IsToolActivity));
                CollectionAssert.AreEqual(
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .OrderByDescending(value => value)
                        .ToArray(),
                    viewModel.FilteredActivityHistory
                        .Select(row => row.StartedSortTimestamp)
                        .ToArray());
                viewModel.ShowActivityTextFilter = false;
                Assert.AreEqual(1, viewModel.FilteredActivityHistory.Count);
                Assert.IsTrue(viewModel.FilteredActivityHistory[0].IsStepActivity);

                // Clearing the final selected category is allowed.
                viewModel.ShowActivityStepsFilter = false;
                Assert.IsFalse(viewModel.ShowActivityStepsFilter);
                Assert.AreEqual(0, viewModel.FilteredActivityHistory.Count);

                // Counts never depend on the current filters.
                Assert.AreEqual("Text (✓2)", viewModel.ActivityTextFilterLabel);
                Assert.AreEqual("Tools (✓1 · ✕1)", viewModel.ActivityToolsFilterLabel);
                Assert.AreEqual("Steps (1)", viewModel.ActivityStepsFilterLabel);

                // The grid's selection binding keeps the visible selected row.
                var grid = (DataGrid)window.FindName("LiveActivityHistoryGrid");
                Assert.IsNotNull(grid);
                PumpDispatcher();
                window.UpdateLayout();
                Assert.IsNull(rowA.SelectedActivity);
                Assert.IsNull(grid.SelectedItem);

                // Selecting another job resets the filter defaults.
                viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == "jobB");
                Assert.IsTrue(viewModel.ShowActivityTextFilter);
                Assert.IsFalse(viewModel.ShowActivityToolsFilter);
                Assert.IsFalse(viewModel.ShowActivityStepsFilter);
                Assert.AreEqual(1, viewModel.FilteredActivityHistory.Count);
                Assert.AreEqual("jobB", viewModel.FilteredActivityHistory[0].Entry.JobId);

                // Job A's own rows are untouched by the filter projection.
                Assert.AreEqual(5, rowA.LiveActivityHistory.Count);
            }
            finally
            {
                window.Close();
            }
        }

        [TestMethod]
        public void V2ToolRecords_FlowThroughSnapshotRowsAndFilteredActivityHistory()
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            var timestamp = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
            var start = Now.AddMinutes(-4).ToUnixTimeMilliseconds();
            var end = start + 4_000;
            var lines = new List<string>(138);
            for (var index = 0; index < 132; index++)
            {
                lines.Add(ToolRecordV2(
                    timestamp + index,
                    "shell",
                    "completed",
                    start,
                    end,
                    part: $"p-ok-{index}",
                    callId: $"call-ok-{index}"));
            }

            for (var index = 0; index < 6; index++)
            {
                lines.Add(ToolRecordV2(
                    timestamp + 132 + index,
                    "edit",
                    "error",
                    null,
                    null,
                    part: $"p-error-{index}",
                    callId: $"call-error-{index}"));
            }

            workspace.WriteLog("job", lines.ToArray());

            var activity = Read(workspace, Prepare(workspace));
            Assert.AreEqual(138, activity.History.Count);
            Assert.AreEqual(138, activity.HistoryRecords.Count);

            var jobSnapshot = Snapshot("job") with
            {
                SessionId = "ses",
                ObservedAt = Now,
                LiveActivity = activity
            };
            using var viewModel = new MainViewModel(
                () => throw new InvalidOperationException(
                    "The monitoring service is not used in this presentation test."),
                Dispatcher.CurrentDispatcher,
                workspace.RunsRoot);
            viewModel.ApplySnapshot(new MonitoringSnapshot
            {
                ObservedAt = Now,
                State = MonitoringState.Monitoring,
                Jobs = [jobSnapshot],
                Warnings = Array.Empty<string>()
            });

            var row = viewModel.Jobs.Single(job => job.JobId == "job");
            Assert.AreEqual(138, row.LiveActivityHistory.Count);
            Assert.AreEqual("Text (✓0)", viewModel.ActivityTextFilterLabel);
            Assert.AreEqual("Tools (✓132 · ✕6)", viewModel.ActivityToolsFilterLabel);
            Assert.AreEqual("Steps (0)", viewModel.ActivityStepsFilterLabel);

            // Tool rows are hidden by the text-only default until Tools is enabled.
            Assert.AreEqual(0, viewModel.FilteredActivityHistory.Count);
            viewModel.ShowActivityToolsFilter = true;

            var visible = viewModel.FilteredActivityHistory;
            Assert.AreEqual(138, visible.Count);
            Assert.AreEqual(
                138,
                visible.Select(candidate => candidate.Entry.PartId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.AreEqual(132, visible.Count(candidate => candidate.IsSuccessfulToolActivity));
            Assert.AreEqual(6, visible.Count(candidate => candidate.IsErrorToolActivity));
            Assert.IsTrue(visible.All(candidate => candidate.IsToolActivity));
            Assert.IsFalse(visible.Any(candidate =>
                candidate.IsTextActivity || candidate.IsStepActivity));

            var completed = visible.Single(candidate => candidate.Entry.PartId == "p-ok-0");
            Assert.AreEqual("shell · completed", completed.ReportedStatusText);
            Assert.AreEqual(4d, completed.DurationSortSeconds);
            var failed = visible.Single(candidate => candidate.Entry.PartId == "p-error-0");
            Assert.AreEqual("edit · error", failed.ReportedStatusText);
            Assert.AreEqual("N/A", failed.DurationText);
        }
    }

    // ---------------- fixtures ----------------

    private static string ToolRecord(
        long timestamp,
        string name,
        string? status,
        long? start,
        long? end,
        string session = "ses",
        string part = "tool-part",
        string message = "msg",
        string? callId = null)
    {
        var partObject = new Dictionary<string, object?>
        {
            ["id"] = part,
            ["messageID"] = message,
            ["type"] = "tool",
            ["tool"] = name
        };
        if (callId is not null)
        {
            partObject["callID"] = callId;
        }
        var state = new Dictionary<string, object?>();
        if (status is not null)
        {
            state["status"] = status;
        }

        if (start is not null && end is not null)
        {
            state["time"] = new Dictionary<string, object?> { ["start"] = start, ["end"] = end };
        }

        partObject["state"] = state;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["role"] = "assistant",
            ["part"] = partObject
        });
    }

    /// <summary>
    /// OpenCode 2.x tool record: <c>partID</c> is the part identity and
    /// <c>id</c> is the distinct tool-call identity.
    /// </summary>
    private static string ToolRecordV2(
        long timestamp,
        string name,
        string? status,
        long? start,
        long? end,
        string session = "ses",
        string part = "tool-part",
        string message = "msg",
        string callId = "call-part")
    {
        var partObject = new Dictionary<string, object?>
        {
            ["id"] = callId,
            ["partID"] = part,
            ["messageID"] = message,
            ["type"] = "tool",
            ["tool"] = name
        };
        var state = new Dictionary<string, object?>();
        if (status is not null)
        {
            state["status"] = status;
        }

        if (start is not null && end is not null)
        {
            state["time"] = new Dictionary<string, object?> { ["start"] = start, ["end"] = end };
        }

        partObject["state"] = state;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["timestamp"] = timestamp,
            ["sessionID"] = session,
            ["role"] = "assistant",
            ["part"] = partObject
        });
    }

    private static string RawToolRecordV2(
        long timestamp,
        string partIdentityProperties,
        string callProperties = "\"id\":\"call-raw\"",
        string message = "msg") =>
        "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
        ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{" + callProperties +
        "," + partIdentityProperties + ",\"messageID\":\"" + message + "\",\"type\":\"tool\",\"tool\":\"shell\"}}";

    private static string RawToolRecord(
        long timestamp,
        string name,
        string status,
        string startLiteral,
        string endLiteral,
        string part) =>
        "{\"type\":\"tool_use\",\"timestamp\":" + timestamp +
        ",\"sessionID\":\"ses\",\"role\":\"assistant\",\"part\":{\"id\":\"" + part +
        "\",\"messageID\":\"msg\",\"type\":\"tool\",\"tool\":\"" + name +
        "\",\"state\":{\"status\":\"" + status + "\",\"time\":{\"start\":" + startLiteral +
        ",\"end\":" + endLiteral + "}}}}";

    private static LiveActivityHistoryEntry Entry(
        string jobId,
        string session,
        string message,
        string partId,
        LiveActivityKind kind,
        long offset,
        DateTimeOffset? timestamp,
        string kindText,
        string text) =>
        new(
            jobId,
            session,
            message,
            partId,
            kindText,
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            "D" + partId,
            null)
        {
            ActivityKind = kind
        };

    private static LiveActivityHistoryEntry Step(
        string jobId,
        string session,
        string message,
        string partId,
        LiveActivityKind kind,
        long offset,
        DateTimeOffset? timestamp,
        string? reason = null,
        bool conflict = false,
        string? digest = null) =>
        new(
            jobId,
            session,
            message,
            partId,
            kind == LiveActivityKind.StepStart ? StepStartKind : StepFinishKind,
            kind == LiveActivityKind.StepStart ? "Session step started" : "Step finished",
            SourcePath,
            offset,
            timestamp,
            Now,
            digest ?? "D" + partId,
            null)
        {
            ActivityKind = kind,
            StepFinishReason = reason,
            StepIdentityConflict = conflict
        };

    private static LiveActivityHistoryEntry Tool(
        string jobId,
        string session,
        string message,
        string partId,
        long offset,
        DateTimeOffset? timestamp,
        string name,
        string status,
        LiveActivityToolOutcome outcome) =>
        new(
            jobId,
            session,
            message,
            partId,
            ToolKind,
            $"Tool {name} {status}",
            SourcePath,
            offset,
            timestamp,
            Now,
            "D" + partId,
            null)
        {
            ActivityKind = LiveActivityKind.Tool,
            ToolName = name,
            ToolStatus = status,
            ToolOutcome = outcome
        };

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static LiveActivitySnapshot SnapshotWith(
        IReadOnlyList<LiveActivityHistoryEntry> entries,
        long start,
        long end) =>
        new()
        {
            History = entries,
            HistoryRecords = entries,
            ReadWindowStart = start,
            ReadWindowEnd = end
        };

    private static LedgerSourceObservation Observation(long captured) =>
        new("job", SourcePath, Identity, Identity, 1, captured, captured, false);

    private static ForgeJobSnapshot Snapshot(string jobId, params LiveActivityHistoryEntry[] entries) =>
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
            LastActivity = Started.AddMinutes(5),
            ObservedAt = Now,
            LiveActivity = new LiveActivitySnapshot
            {
                History = entries,
                Availability = "Bounded synthetic activity."
            }
        };

    private static Prepared Prepare(TestWorkspace workspace, string jobId = "job")
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == jobId);
        var database = Path.Combine(workspace.Root, "phase2-activity.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var result = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation);
        return new Prepared(job, evidence[jobId], result.SourceObservation!);
    }

    private static LiveActivitySnapshot Read(
        TestWorkspace workspace,
        Prepared prepared,
        long maxBytes = JobLiveActivityReader.MaxHistoryReadBytes) =>
        JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            maxBytes: maxBytes);

    private sealed record Prepared(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
