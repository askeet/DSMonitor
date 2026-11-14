using System.IO;
using System.Text;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Synthetic coverage for the 5 MiB Activity History window and its
/// window-bounded retention. No real Forge or OpenCode data is used.
/// </summary>
[TestClass]
public sealed class ActivityHistoryRetentionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private const string SourcePath = @"C:\synthetic\runs\job.log";
    private static readonly LedgerSourceIdentity Identity = new(11, 22, 33);

    [TestMethod]
    public void Reader_RetainsAllPartsInsideFiveMiBIncludingOlderTextRows()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var baseTimestamp = Now.AddMinutes(-30).ToUnixTimeMilliseconds();
        var lines = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            lines.Add(TestWorkspace.Text(
                baseTimestamp + index,
                "old text " + index,
                part: "text-" + index,
                message: "msg-text-" + index));
        }

        for (var index = 0; index < 70; index++)
        {
            var timestamp = baseTimestamp + 100 + index;
            lines.Add(TestWorkspace.ToolUse(
                timestamp,
                "tool-" + index,
                start: timestamp,
                end: timestamp + 2_000,
                part: "tool-part-" + index,
                message: "msg-tool-" + index));
        }

        workspace.WriteLog("job", lines.ToArray());
        var prepared = Prepare(workspace);
        var activity = Read(prepared, workspace);

        Assert.AreEqual(0L, activity.ReadWindowStart);
        Assert.AreEqual(prepared.Observation.CapturedLength, activity.ReadWindowEnd);
        Assert.AreEqual(75, activity.History.Count);
        Assert.AreEqual(
            5,
            activity.History.Count(entry => entry.PartId.StartsWith("text-", StringComparison.Ordinal)));
        Assert.AreEqual(
            70,
            activity.History.Count(entry => entry.PartId.StartsWith("tool-part-", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 5).Select(index => "text-" + index).ToArray(),
            activity.History
                .Where(entry => entry.PartId.StartsWith("text-", StringComparison.Ordinal))
                .Select(entry => entry.PartId)
                .ToArray());

        // Newest source timestamp first; the five older text rows remain.
        Assert.AreEqual("tool-part-69", activity.History[0].PartId);
        Assert.AreEqual("text-0", activity.History[^1].PartId);
        Assert.IsTrue(activity.History.All(entry =>
            entry.ByteOffset >= activity.ReadWindowStart &&
            entry.ByteOffset < activity.ReadWindowEnd));

        var buffer = new LiveActivityHistoryBuffer();
        var retained = buffer.Apply(
            activity,
            "job",
            prepared.Evidence.VerifiedPath!,
            prepared.Observation);
        Assert.AreEqual(75, retained.Count);
        Assert.IsTrue(retained.Any(entry => entry.PartId == "text-0"));
        Assert.IsTrue(retained.Any(entry => entry.PartId == "tool-part-69"));
    }

    [TestMethod]
    public void HistoryBuffer_EvictsOutsideTheAdvancedWindowAndNeverAccumulates()
    {
        var buffer = new LiveActivityHistoryBuffer();
        var a = Entry("A", 100, Now.AddMinutes(-5), part: "a", digest: "A");
        var b = Entry("B", 6_000, Now.AddMinutes(-4), part: "b", digest: "B");
        var c = Entry("C", 9_000, Now.AddMinutes(-3), part: "c", digest: "C");
        var d = Entry("D", 19_000, Now.AddMinutes(-2), part: "d", digest: "D");
        var e = Entry("E", 29_000, Now.AddMinutes(-1), part: "e", digest: "E");

        var seeded = buffer.Apply(
            SnapshotWithRecords([a, b, c], 0, 10_000),
            "job",
            SourcePath,
            Observation(captured: 10_000));
        Assert.AreEqual(3, seeded.Count);

        var advanced = buffer.Apply(
            SnapshotWithRecords([b, c, d], 5_000, 20_000),
            "job",
            SourcePath,
            Observation(captured: 20_000));
        CollectionAssert.AreEqual(
            new[] { "D", "C", "B" },
            advanced.Select(entry => entry.Text).ToArray());
        Assert.IsFalse(advanced.Any(entry => entry.PartId == "a"));

        var moved = buffer.Apply(
            SnapshotWithRecords([d, e], 15_000, 30_000),
            "job",
            SourcePath,
            Observation(captured: 30_000));
        CollectionAssert.AreEqual(
            new[] { "E", "D" },
            moved.Select(entry => entry.Text).ToArray());
        Assert.IsFalse(moved.Any(entry => entry.PartId is "b" or "c"));

        // A repeated refresh of the same window may not accumulate stale rows.
        var repeated = buffer.Apply(
            SnapshotWithRecords([d, e], 15_000, 30_000),
            "job",
            SourcePath,
            Observation(captured: 30_000));
        CollectionAssert.AreEqual(
            new[] { "E", "D" },
            repeated.Select(entry => entry.Text).ToArray());
    }

    [TestMethod]
    public void Reader_WindowBoundaryKeepsACompleteRecordAndDropsPartialRecords()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var a = TestWorkspace.Text(Now.AddMinutes(-3).ToUnixTimeMilliseconds(), "alpha", part: "a", message: "ma");
        var b = TestWorkspace.Text(Now.AddMinutes(-2).ToUnixTimeMilliseconds(), "beta", part: "b", message: "mb");
        var c = TestWorkspace.Text(Now.AddMinutes(-1).ToUnixTimeMilliseconds(), "gamma", part: "c", message: "mc");
        var content = a + "\n" + b + "\n" + c + "\n";
        File.WriteAllText(
            workspace.LogPath("job"), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var prepared = Prepare(workspace);
        var total = Encoding.UTF8.GetByteCount(content);

        // The full window ends exactly at C's terminator, so C is complete.
        var complete = Read(prepared, workspace, total);
        Assert.AreEqual(0L, complete.ReadWindowStart);
        CollectionAssert.AreEquivalent(
            new[] { "a", "b", "c" },
            complete.History.Select(entry => entry.PartId).ToArray());

        // Starting one byte into A discards the partial first record; the
        // window still reaches C's terminator, so B and C remain complete.
        var partialFirst = Read(prepared, workspace, total - 1);
        Assert.AreEqual(1L, partialFirst.ReadWindowStart);
        Assert.AreEqual(total, partialFirst.ReadWindowEnd);
        CollectionAssert.AreEqual(
            new[] { "c", "b" },
            partialFirst.History.Select(entry => entry.PartId).ToArray());

        // A window that starts exactly at B's first byte still cannot prove
        // that boundary without the preceding byte, so B is conservatively
        // discarded while the following complete record remains.
        var bStart = Encoding.UTF8.GetByteCount(a + "\n");
        var atBoundary = Read(prepared, workspace, total - bStart);
        Assert.AreEqual(bStart, atBoundary.ReadWindowStart);
        CollectionAssert.AreEqual(
            new[] { "c" },
            atBoundary.History.Select(entry => entry.PartId).ToArray());

        // An unterminated final line is never parsed even when the window
        // starts at the beginning of the file.
        using var unterminatedWorkspace = new TestWorkspace();
        unterminatedWorkspace.WriteMetadata("job");
        File.WriteAllText(
            unterminatedWorkspace.LogPath("job"),
            a + "\n" + b,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var unterminated = Read(Prepare(unterminatedWorkspace), unterminatedWorkspace);
        Assert.AreEqual(0L, unterminated.ReadWindowStart);
        CollectionAssert.AreEqual(
            new[] { "a" },
            unterminated.History.Select(entry => entry.PartId).ToArray());
    }

    [TestMethod]
    public void Reader_LargeLogReadsOnlyTheLatestFiveMiBAndKeepsTheNewestPreview()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var timestamp = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var lines = new List<string>
        {
            TestWorkspace.Text(timestamp, "outside window", part: "old", message: "m-old")
        };
        for (var index = 0; index < 14; index++)
        {
            lines.Add(TestWorkspace.Text(
                timestamp + 1 + index,
                new string('f', 400 * 1024),
                part: "filler-" + index,
                message: "m-filler-" + index));
        }

        lines.Add(TestWorkspace.Text(timestamp + 100, "inside window", part: "new", message: "m-new"));
        workspace.WriteLog("job", lines.ToArray());

        var prepared = Prepare(workspace);
        var activity = Read(prepared, workspace);

        Assert.IsTrue(activity.ReadWindowStart > 0);
        Assert.AreEqual(
            prepared.Observation.CapturedLength - JobLiveActivityReader.MaxHistoryReadBytes,
            activity.ReadWindowStart);
        Assert.IsFalse(activity.History.Any(entry => entry.PartId == "old"));
        Assert.IsTrue(activity.History.Any(entry => entry.PartId == "new"));
        Assert.AreEqual("inside window", activity.Doing?.Text);
    }

    [TestMethod]
    public void Cancellation_StopsALargeWindowBeforeScanningRecords()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        for (var index = 0; index < 14; index++)
        {
            workspace.AppendLog(
                "job",
                TestWorkspace.Text(
                    Now.AddMinutes(-1).AddSeconds(index).ToUnixTimeMilliseconds(),
                    new string('x', 400 * 1024),
                    part: "long-" + index,
                    message: "m-" + index));
        }

        var prepared = Prepare(workspace);
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsException<OperationCanceledException>(() => JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            cancellation.Token,
            JobLiveActivityReader.MaxHistoryReadBytes,
            beforeContentRead: cancellation.Cancel,
            beforePublishCheck: null));
    }

    private static Prepared Prepare(TestWorkspace workspace, string jobId = "job")
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == jobId);
        var database = Path.Combine(workspace.Root, "activity-retention.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var result = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation);
        return new Prepared(job, evidence[jobId], result.SourceObservation!);
    }

    private static LiveActivitySnapshot Read(
        Prepared prepared,
        TestWorkspace workspace,
        long maxBytes = JobLiveActivityReader.MaxHistoryReadBytes) =>
        JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            CancellationToken.None,
            maxBytes,
            beforeContentRead: null,
            beforePublishCheck: null);

    private static LiveActivityHistoryEntry Entry(
        string text,
        long offset,
        DateTimeOffset timestamp,
        string part,
        string digest,
        string message = "msg") =>
        new(
            "job",
            "ses",
            message,
            part,
            "Assistant-reported text",
            text,
            SourcePath,
            offset,
            timestamp,
            Now,
            digest,
            null);

    private static LiveActivitySnapshot SnapshotWithRecords(
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
        new(
            "job",
            SourcePath,
            Identity,
            Identity,
            1,
            captured,
            captured,
            false);

    private sealed record Prepared(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
