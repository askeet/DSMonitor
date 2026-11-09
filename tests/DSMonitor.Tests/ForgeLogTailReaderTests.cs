using System.Text;
using DSMonitor.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ForgeLogTailReaderTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Poll_PartialEvent_IsNotEmittedBeforeNewline()
    {
        var jobId = "job_partial_0001";
        var reader = CreateReader(jobId);
        File.WriteAllText(_workspace.LogPath(jobId), TestWorkspace.StepStart(1_800_000_000_000));

        var first = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(0, first.NewEvents.Count);
        Assert.AreEqual(0, reader.TotalUniqueEvents);

        File.AppendAllText(_workspace.LogPath(jobId), "\n");
        var second = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, second.NewEvents.Count);
        Assert.AreEqual(1, reader.TotalUniqueEvents);
    }

    [TestMethod]
    public void Poll_AppendIncrementally_DoesNotDuplicateEvents()
    {
        var jobId = "job_append_0001";
        var reader = CreateReader(jobId);
        var line1 = TestWorkspace.StepStart(1_800_000_000_000, part: "p1");
        var line2 = TestWorkspace.Text(1_800_000_000_500, "some output", part: "p2");

        File.WriteAllText(_workspace.LogPath(jobId), line1 + "\n");
        Assert.AreEqual(1, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);
        Assert.AreEqual(0, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        File.AppendAllText(_workspace.LogPath(jobId), line2 + "\n");
        var second = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, second.NewEvents.Count);
        Assert.AreEqual(2, reader.TotalUniqueEvents);
        Assert.AreEqual(0, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);
    }

    [TestMethod]
    public void Poll_DuplicateLine_EmitsOnlyOnce()
    {
        var jobId = "job_duplicate_0001";
        var reader = CreateReader(jobId);
        var line = TestWorkspace.StepStart(1_800_000_000_000);
        File.WriteAllText(_workspace.LogPath(jobId), line + "\n" + line + "\n");

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, result.NewEvents.Count);
        Assert.AreEqual(1, reader.TotalUniqueEvents);
    }

    [TestMethod]
    public void Poll_ReplayAfterBoundedDedupeWindow_IsReemittedByReader()
    {
        var jobId = "job_replay_reader_0001";
        var highTimestamp = 1_800_100_000_000;
        var highLine = TestWorkspace.StepStart(
            highTimestamp,
            part: "high-retained",
            message: "high-retained");
        var builder = new StringBuilder();
        builder.Append(highLine).Append('\n');
        for (var index = 1; index <= 10_000; index++)
        {
            builder.Append(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "reader-replay-" + index.ToString("0000"),
                message: "reader-replay-" + index.ToString("0000")));
            builder.Append('\n');
        }

        File.WriteAllText(_workspace.LogPath(jobId), builder.ToString());
        var reader = CreateReader(jobId);
        var emitted = 0;
        while (reader.Offset < new FileInfo(_workspace.LogPath(jobId)).Length)
        {
            emitted += reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count;
        }

        Assert.AreEqual(10_001, emitted);
        Assert.AreEqual(10_001, reader.TotalUniqueEvents);
        Assert.IsTrue(ForgeEventParser.TryParse(
            highLine,
            jobId,
            DateTimeOffset.UtcNow,
            out var parsed));

        File.AppendAllText(_workspace.LogPath(jobId), highLine + "\n");
        var replay = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(
            1,
            replay.NewEvents.Count,
            "the reader's bounded 10,000-key window is expected to re-emit the evicted key");
        Assert.AreEqual(parsed!.EventKey, replay.NewEvents[0].EventKey);
        Assert.AreEqual(10_002, reader.TotalUniqueEvents);
    }

    [TestMethod]
    public void Poll_RejectedCostEvent_DoesNotUpdateAggregates()
    {
        var jobId = "job_accept_gate_0001";
        var rejectedLine = TestWorkspace.StepFinish(
            1_800_000_000_000,
            0.375,
            session: "ses_rejected",
            part: "rejected-finish",
            message: "rejected-message");
        File.WriteAllText(_workspace.LogPath(jobId), rejectedLine + "\n");
        var reader = CreateReader(jobId);

        var rejected = reader.Poll(DateTimeOffset.UtcNow, _ => false);

        Assert.AreEqual(0, rejected.NewEvents.Count);
        Assert.AreEqual(0, reader.TotalUniqueEvents);
        Assert.AreEqual(0, reader.TotalCost);
        Assert.IsFalse(reader.HasCost);
        Assert.IsNull(reader.LatestSessionId);
        Assert.IsNull(reader.LastActivity);

        var acceptedLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.125,
            session: "ses_accepted",
            part: "accepted-finish",
            message: "accepted-message");
        File.AppendAllText(_workspace.LogPath(jobId), acceptedLine + "\n");
        var accepted = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, accepted.NewEvents.Count);
        Assert.AreEqual(1, reader.TotalUniqueEvents);
        Assert.AreEqual(0.125, reader.TotalCost, 1e-12);
        Assert.IsTrue(reader.HasCost);
        Assert.AreEqual("ses_accepted", reader.LatestSessionId);
        Assert.IsNotNull(reader.LastActivity);
    }

    [TestMethod]
    public void Poll_MalformedLine_IsSkippedAndParsingContinues()
    {
        var jobId = "job_malformed_0001";
        var reader = CreateReader(jobId);
        File.WriteAllText(
            _workspace.LogPath(jobId),
            "not-json\n" + TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, result.NewEvents.Count);
        Assert.AreEqual(1, result.MalformedLinesThisPoll);
        Assert.AreEqual(1, reader.MalformedLineCount);
    }

    [TestMethod]
    public void Poll_Truncation_ResetsAndReadsNewContent()
    {
        var jobId = "job_truncate_0001";
        var reader = CreateReader(jobId);
        File.WriteAllText(
            _workspace.LogPath(jobId),
            TestWorkspace.StepStart(1_800_000_000_000, part: "p1") + "\n" +
            TestWorkspace.Text(1_800_000_000_500, "first", part: "p2") + "\n");
        Assert.AreEqual(2, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        File.WriteAllText(
            _workspace.LogPath(jobId),
            TestWorkspace.Text(1_800_000_001_000, "after truncation", part: "p3") + "\n");

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.IsTrue(result.ResetDetected);
        Assert.AreEqual(1, reader.ResetCount);
        Assert.AreEqual(1, result.NewEvents.Count);
        Assert.AreEqual(3, reader.TotalUniqueEvents);
    }

    [TestMethod]
    public void Poll_Replacement_DetectsNewFileAndAvoidsDuplicateReplay()
    {
        var jobId = "job_replace_0001";
        var reader = CreateReader(jobId);
        var firstLine = TestWorkspace.StepStart(1_800_000_000_000, part: "p1");
        File.WriteAllText(_workspace.LogPath(jobId), firstLine + "\n");
        Assert.AreEqual(1, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        var replacement = Path.Combine(_workspace.RunsRoot, "replacement.tmp");
        File.WriteAllText(
            replacement,
            firstLine + "\n" + TestWorkspace.Text(1_800_000_000_500, "new", part: "p2") + "\n");
        File.Move(replacement, _workspace.LogPath(jobId), overwrite: true);

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.IsTrue(reader.ResetCount >= 1);
        Assert.AreEqual(1, result.NewEvents.Count);
        Assert.AreEqual(2, reader.TotalUniqueEvents);
    }

    [TestMethod]
    public void Poll_MissingFile_DoesNotThrow()
    {
        var reader = CreateReader("job_missing_0001");

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.IsTrue(result.LogMissing);
        Assert.IsTrue(reader.IsMissing);
        Assert.AreEqual(0, result.NewEvents.Count);
    }

    [TestMethod]
    public void Poll_OutsideRootPath_IsNotRead()
    {
        var outside = Path.Combine(_workspace.Root, "outside.log");
        File.WriteAllText(outside, TestWorkspace.StepStart(1_800_000_000_000) + "\n");
        var reader = new ForgeLogTailReader("job_outside_0001", outside, _workspace.RunsRoot);

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.IsFalse(reader.IsPathAllowed);
        Assert.AreEqual(0, result.NewEvents.Count);
        Assert.IsTrue(result.LogMissing);
    }

    [TestMethod]
    public void Poll_RespectsMaxBytesPerPoll()
    {
        var jobId = "job_large_0001";
        var builder = new StringBuilder();
        for (var index = 0; index < 120; index++)
        {
            builder.Append(TestWorkspace.Text(
                1_800_000_000_000 + index,
                "bounded output",
                part: "p" + index,
                message: "m" + index));
            builder.Append('\n');
        }

        File.WriteAllText(_workspace.LogPath(jobId), builder.ToString());
        var reader = new ForgeLogTailReader(
            jobId,
            _workspace.LogPath(jobId),
            _workspace.RunsRoot,
            maxBytesPerPoll: 4096);

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.IsTrue(result.BytesRead <= 4096);
        Assert.IsTrue(reader.Offset < new FileInfo(_workspace.LogPath(jobId)).Length);
        Assert.IsTrue(result.NewEvents.Count > 0);
    }

    [DataTestMethod]
    [DataRow(500)]
    [DataRow(501)]
    [DataRow(800)]
    public void Poll_LargeBatch_ReturnsEveryAcceptedEvent(int eventCount)
    {
        var jobId = "job_batch_" + eventCount;
        var lines = BuildEventLines(eventCount);
        File.WriteAllText(_workspace.LogPath(jobId), string.Join("\n", lines) + "\n");
        var reader = CreateReader(jobId);

        var result = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(eventCount, result.NewEvents.Count);
        Assert.AreEqual(eventCount, reader.TotalUniqueEvents);
        Assert.AreEqual(new FileInfo(_workspace.LogPath(jobId)).Length, reader.Offset);
        Assert.AreEqual(0, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);
    }

    [TestMethod]
    public void Poll_SuccessiveLargeBatches_AreDeliveredWithoutLossOrDuplicates()
    {
        var jobId = "job_batch_sequence";
        var reader = CreateReader(jobId);

        File.WriteAllText(
            _workspace.LogPath(jobId),
            string.Join("\n", BuildEventLines(0, 300)) + "\n");
        Assert.AreEqual(300, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        File.AppendAllText(
            _workspace.LogPath(jobId),
            string.Join("\n", BuildEventLines(300, 300)) + "\n");
        Assert.AreEqual(300, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        File.AppendAllText(
            _workspace.LogPath(jobId),
            string.Join("\n", BuildEventLines(600, 200)) + "\n");
        Assert.AreEqual(200, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        Assert.AreEqual(800, reader.TotalUniqueEvents);
        Assert.AreEqual(0, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);
    }

    [TestMethod]
    public void ReadTailPreview_IsBoundedAndConfinedToRunsRoot()
    {
        var jobId = "job_preview_0001";
        var builder = new StringBuilder();
        for (var index = 0; index < 80; index++)
        {
            builder.Append(TestWorkspace.Text(1_800_000_000_000 + index, new string('x', 200), part: "p" + index));
            builder.Append('\n');
        }

        File.WriteAllText(_workspace.LogPath(jobId), builder.ToString());

        var preview = ForgeLogTailReader.ReadTailPreview(_workspace.LogPath(jobId), _workspace.RunsRoot, 2048);
        var outside = ForgeLogTailReader.ReadTailPreview(
            Path.Combine(_workspace.Root, "outside.log"),
            _workspace.RunsRoot);

        Assert.IsNotNull(preview);
        Assert.IsTrue(preview.Length <= 2048);
        Assert.IsNull(outside);
    }

    private ForgeLogTailReader CreateReader(string jobId) =>
        new(
            jobId,
            _workspace.LogPath(jobId),
            _workspace.RunsRoot,
            maxBytesPerPoll: 256 * 1024);

    private static string[] BuildEventLines(int eventCount) =>
        BuildEventLines(0, eventCount);

    private static string[] BuildEventLines(int startIndex, int count)
    {
        var lines = new string[count];
        for (var index = 0; index < count; index++)
        {
            var sequence = startIndex + index;
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + sequence,
                part: "p" + sequence,
                message: "m" + sequence);
        }

        return lines;
    }
}
