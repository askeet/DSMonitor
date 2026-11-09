using System.Text;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class B2ProjectionLedgerTests
{
    private const string JobId = "job_b2_0001";

    private TestWorkspace _workspace = null!;
    private string _databasePath = null!;
    private string _b1Path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _b1Path = Path.Combine(_workspace.Root, "data", "events.db");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Open_FreshDatabase_CreatesV2AndReopensWithAggregates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var b1Bytes = Encoding.UTF8.GetBytes("B1 database must not be touched");
        File.WriteAllBytes(_b1Path, b1Bytes);
        var logPath = _workspace.LogPath("job_b2_reopen");
        _workspace.WriteLog(
            "job_b2_reopen",
            TestWorkspace.StepFinish(1_800_000_001_000, 0.25m, session: "ses_b2_reopen"));

        using (var ledger = CreateLedger())
        {
            var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
            Assert.IsNotNull(result.Aggregate);
            Assert.AreEqual(1, result.Aggregate!.LogicalEventCount);
            Assert.AreEqual(0.25m, result.Aggregate.CostTotal);
            Assert.AreEqual("ses_b2_reopen", result.Aggregate.LatestSessionId);
            Assert.IsTrue(result.Bootstrap!.IsComplete);
        }

        using (var reopened = CreateLedger())
        {
            var aggregate = reopened.GetJobAggregate(JobId);
            Assert.AreEqual(1, aggregate.LogicalEventCount);
            Assert.AreEqual(0.25m, aggregate.CostTotal);
            Assert.AreEqual("ses_b2_reopen", aggregate.LatestSessionId);
            Assert.AreEqual(1, reopened.ListTimelineEvents(JobId, 500).Count);
            Assert.AreEqual(1, reopened.ListBootstrapProgress(JobId).Count);
            Assert.IsTrue(reopened.ListBootstrapProgress(JobId).Single().IsComplete);
        }

        CollectionAssert.AreEqual(b1Bytes, File.ReadAllBytes(_b1Path));
    }

    [TestMethod]
    public void Open_InvalidDatabase_FailsClosedWithoutMutation()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var invalid = Encoding.UTF8.GetBytes("this is not a sqlite database");
        File.WriteAllBytes(_databasePath, invalid);

        Assert.ThrowsException<LedgerException>(() => CreateLedger());
        CollectionAssert.AreEqual(invalid, File.ReadAllBytes(_databasePath));
    }

    [TestMethod]
    public void Ingest_LogicalDuplicateAtDifferentOffsets_AppliesOnce()
    {
        var logPath = _workspace.LogPath("job_b2_duplicate_offsets");
        var line = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.75m,
            session: "ses_b2_dup",
            part: "part-duplicate",
            message: "message-duplicate");
        File.WriteAllText(logPath, line + "\n" + line + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(2, result.RecordsCommitted);
        Assert.AreEqual(2, result.EventsCommitted);
        Assert.AreEqual(1, result.AcceptedEvents.Count);
        Assert.IsNotNull(result.Aggregate);
        Assert.AreEqual(1, result.Aggregate!.LogicalEventCount);
        Assert.AreEqual(0.75m, result.Aggregate.CostTotal);
        Assert.AreEqual(2, ledger.ListRecords(result.GenerationId!.Value).Count);
        Assert.AreEqual(LedgerCoverageState.Clean, ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void Ingest_SameLogicalEventCopiedToAnotherFile_AppliesOnce()
    {
        var sourcePath = _workspace.LogPath("job_b2_copy_source");
        var copyPath = _workspace.LogPath("job_b2_copy_target");
        var line = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.75m,
            session: "ses_b2_copy",
            part: "part-copy",
            message: "message-copy");
        File.WriteAllText(sourcePath, line + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var original = ledger.Ingest(JobId, sourcePath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, original.Status);

        File.Copy(sourcePath, copyPath);
        var copy = ledger.Ingest(JobId, copyPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, copy.Status);
        Assert.AreEqual(1, copy.RecordsCommitted);
        Assert.AreEqual(1, copy.EventsCommitted);
        Assert.AreEqual(0, copy.AcceptedEvents.Count);
        Assert.AreNotEqual(original.GenerationId, copy.GenerationId);
        Assert.AreEqual(2, ledger.ListGenerations(JobId).Count);

        var aggregate = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(1, aggregate.LogicalEventCount);
        Assert.AreEqual(0.75m, aggregate.CostTotal);
        Assert.AreEqual("ses_b2_copy", aggregate.LatestSessionId);
    }

    [TestMethod]
    public void Ingest_DistinctSourceIdsWithSameCost_AreDistinct()
    {
        var logPath = _workspace.LogPath("job_b2_distinct");
        _workspace.WriteLog(
            "job_b2_distinct",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.50m,
                part: "part-a",
                message: "message-a",
                session: "ses_b2_distinct"),
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.50m,
                part: "part-b",
                message: "message-b",
                session: "ses_b2_distinct"));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(2, result.AcceptedEvents.Count);
        var aggregate = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(2, aggregate.LogicalEventCount);
        Assert.AreEqual(1.00m, aggregate.CostTotal);
    }

    [TestMethod]
    public void Ingest_SeparateJobsSharingSourceIds_RemainDistinct()
    {
        var logPath = _workspace.LogPath("job_b2_shared");
        _workspace.WriteLog(
            "job_b2_shared",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.75m,
                part: "shared-part",
                message: "shared-message",
                session: "ses_shared"));

        using var ledger = CreateLedger();
        var first = ledger.Ingest("job_b2_job_a", logPath, _workspace.RunsRoot);
        var second = ledger.Ingest("job_b2_job_b", logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        Assert.AreEqual(LedgerIngestStatus.Committed, second.Status);
        Assert.AreEqual(1, ledger.GetJobAggregate("job_b2_job_a").LogicalEventCount);
        Assert.AreEqual(1, ledger.GetJobAggregate("job_b2_job_b").LogicalEventCount);
        Assert.AreEqual(0.75m, ledger.GetJobAggregate("job_b2_job_a").CostTotal);
        Assert.AreEqual(0.75m, ledger.GetJobAggregate("job_b2_job_b").CostTotal);
    }

    [TestMethod]
    public void Ingest_ReplayAfterMoreThanTenThousandEvents_KeepsCostAndSession()
    {
        var logPath = _workspace.LogPath("job_b2_replay");
        var highLine = TestWorkspace.StepFinish(
            1_800_000_900_000,
            0.75m,
            session: "ses_cost_original",
            part: "cost-original",
            message: "msg-cost-original");
        var lines = new List<string> { highLine };
        for (var index = 0; index < 10_001; index++)
        {
            lines.Add(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                session: "ses_cost_continued",
                part: "continuation-" + index.ToString("00000"),
                message: "msg-continuation-" + index.ToString("00000")));
        }

        lines.Add(highLine);
        File.WriteAllText(
            logPath,
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger(new EventLedgerOptions
        {
            MaxBytesPerTransaction = 512 * 1024
        });
        IngestAll(ledger, JobId, logPath);

        var aggregate = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(10_002, aggregate.LogicalEventCount);
        Assert.AreEqual(0.75m, aggregate.CostTotal);
        Assert.AreEqual("ses_cost_continued", aggregate.LatestSessionId);
        Assert.AreEqual(
            1,
            ledger.ListTimelineEvents(JobId, 500)
                .Count(item => item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
    }

    [TestMethod]
    public void Bootstrap_SmallBatchesResumeAfterReopen()
    {
        var logPath = _workspace.LogPath("job_b2_resume");
        var lines = new string[20];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "resume-" + index.ToString("000"),
                message: "msg-resume-" + index.ToString("000"));
        }

        _workspace.WriteLog("job_b2_resume", lines);
        var options = new EventLedgerOptions
        {
            MaxBytesPerTransaction = 512,
            MaxLineBytes = 4096
        };

        long firstOffset;
        using (var first = CreateLedger(options))
        {
            var result = first.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
            firstOffset = result.CommittedOffset;
            Assert.IsTrue(firstOffset < new FileInfo(logPath).Length);
            Assert.IsFalse(first.ListBootstrapProgress(JobId).Single().IsComplete);
        }

        using (var reopened = CreateLedger(new EventLedgerOptions
        {
            MaxBytesPerTransaction = 1024 * 1024
        }))
        {
            var next = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, next.Status);
            Assert.IsTrue(next.CommittedOffset > firstOffset);
            var aggregate = reopened.GetJobAggregate(JobId);
            Assert.AreEqual(lines.Length, aggregate.LogicalEventCount);
            Assert.IsTrue(reopened.ListBootstrapProgress(JobId).Single().IsComplete);

            var noData = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.NoData, noData.Status);
            Assert.AreEqual(lines.Length, reopened.GetJobAggregate(JobId).LogicalEventCount);
        }
    }

    [TestMethod]
    public void Bootstrap_AppendBeyondCapturedBoundary_IsProcessedBySamePath()
    {
        var logPath = _workspace.LogPath("job_b2_append_during");
        var initial = new string[5];
        for (var index = 0; index < initial.Length; index++)
        {
            initial[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "append-" + index.ToString("000"),
                message: "msg-append-" + index.ToString("000"));
        }

        _workspace.WriteLog("job_b2_append_during", initial);
        var boundary = new FileInfo(logPath).Length;

        using (var ledger = CreateLedger(new EventLedgerOptions
        {
            MaxBytesPerTransaction = 512,
            MaxLineBytes = 4096
        }))
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            _workspace.AppendLog(
                "job_b2_append_during",
                TestWorkspace.StepStart(
                    1_800_000_100_000,
                    part: "append-live",
                    message: "msg-append-live"));

            IngestAll(ledger, JobId, logPath);

            var bootstrap = ledger.ListBootstrapProgress(JobId).Single();
            Assert.AreEqual(boundary, bootstrap.InitialBoundary);
            Assert.IsTrue(bootstrap.IsComplete);
            Assert.AreEqual(initial.Length + 1, ledger.GetJobAggregate(JobId).LogicalEventCount);
        }
    }

    [TestMethod]
    public void Ingest_MissingIdentity_IsCoveredWithoutAggregateEffects()
    {
        var logPath = _workspace.LogPath("job_b2_missing_identity");
        File.WriteAllText(
            logPath,
            "{\"type\":\"server_connected\",\"timestamp\":1800000000000}\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(1, result.EventsCommitted);
        Assert.AreEqual(0, result.AcceptedEvents.Count);
        Assert.AreEqual(1, ledger.GetJobAggregate(JobId).MissingIdentityCount);
        Assert.AreEqual(0, ledger.GetJobAggregate(JobId).LogicalEventCount);
        Assert.AreEqual(
            LedgerCoverageState.MissingIdentity,
            ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void Ingest_StepFinishWithoutCost_MarksIncompleteWithoutInventingZero()
    {
        var logPath = _workspace.LogPath("job_b2_missing_cost");
        File.WriteAllText(
            logPath,
            """
            {"type":"step_finish","timestamp":1800000000000,"sessionID":"ses_missing_cost","part":{"id":"part-no-cost","messageID":"msg-no-cost","type":"step-finish","reason":"stop"}}
            """ + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        var aggregate = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(1, aggregate.LogicalEventCount);
        Assert.IsFalse(aggregate.HasCost);
        Assert.AreEqual(0m, aggregate.CostTotal);
        Assert.AreEqual(1, aggregate.MissingCostEventCount);
        Assert.AreEqual(LedgerCoverageState.MissingCost, ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void Ingest_MalformedAndPartialTrailing_AreRecordedAsCoverage()
    {
        var logPath = _workspace.LogPath("job_b2_partial");
        var valid = TestWorkspace.StepStart(1_800_000_000_000);
        var partial = "{\"type\":\"text\"";
        File.WriteAllText(
            logPath,
            "not json\n" + valid + "\n" + partial,
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.IsTrue(result.PartialTrailingRecord);
        Assert.AreEqual(1, result.AcceptedEvents.Count);
        Assert.AreEqual(1, ledger.GetJobAggregate(JobId).LogicalEventCount);
        var coverage = ledger.GetJobCoverage(JobId);
        Assert.AreEqual(LedgerCoverageState.Partial, coverage.State);
        Assert.AreEqual(1, coverage.MalformedRecords);

        File.AppendAllText(logPath, "}\n", new UTF8Encoding(false));
        var completed = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, completed.Status);
        Assert.IsFalse(completed.PartialTrailingRecord);
        Assert.AreEqual(0, completed.AcceptedEvents.Count);
        Assert.AreEqual(1, ledger.GetJobAggregate(JobId).LogicalEventCount);
        Assert.AreEqual(LedgerCoverageState.MissingIdentity, ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void Ingest_InjectedCommitFailure_RollsBackRecordsLogicalAggregateCursorAndCheckpoint()
    {
        var logPath = _workspace.LogPath("job_b2_rollback");
        _workspace.WriteLog(
            "job_b2_rollback",
            TestWorkspace.StepStart(1_800_000_000_000, part: "rollback-1", message: "msg-1"));

        using (var first = CreateLedger())
        {
            var initial = first.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, initial.Status);
        }

        _workspace.AppendLog(
            "job_b2_rollback",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.5m,
                part: "rollback-2",
                message: "msg-2"));

        using var ledger = CreateLedger(new EventLedgerOptions
        {
            BeforeCommitFaultInjector = () =>
                throw new LedgerException(
                    LedgerStorageFailure.Unavailable,
                    "Injected commit failure.")
        });

        var before = ledger.GetJobAggregate(JobId);
        var beforeGenerations = ledger.ListGenerations(JobId);
        var beforeRecords = beforeGenerations
            .SelectMany(generation => ledger.ListRecords(generation.GenerationId))
            .Count();
        var beforeBootstrap = ledger.ListBootstrapProgress(JobId).Single();
        var beforeCoverage = ledger.GetJobCoverage(JobId);

        Assert.ThrowsException<LedgerException>(
            () => ledger.Ingest(JobId, logPath, _workspace.RunsRoot));

        var after = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(before.LogicalEventCount, after.LogicalEventCount);
        Assert.AreEqual(before.CostTotal, after.CostTotal);
        Assert.AreEqual(before.Version, after.Version);
        Assert.AreEqual(
            beforeRecords,
            ledger.ListGenerations(JobId)
                .SelectMany(generation => ledger.ListRecords(generation.GenerationId))
                .Count());
        var afterBootstrap = ledger.ListBootstrapProgress(JobId).Single();
        Assert.AreEqual(beforeBootstrap.CommittedOffset, afterBootstrap.CommittedOffset);
        Assert.AreEqual(beforeBootstrap.CompletedAt, afterBootstrap.CompletedAt);
        Assert.AreEqual(beforeCoverage.State, ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void Ingest_ConflictingImmutableMetadata_RollsBackAndReports()
    {
        var logPath = _workspace.LogPath("job_b2_conflict");
        var firstLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.25m,
            part: "conflict-part",
            message: "conflict-message");
        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        var aggregateBefore = ledger.GetJobAggregate(JobId);
        var recordsBefore = ledger.ListRecords(first.GenerationId!.Value).Count;

        _workspace.AppendLog(
            "job_b2_conflict",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.50m,
                part: "conflict-part",
                message: "conflict-message"));

        var exception = Assert.ThrowsException<LedgerException>(
            () => ledger.Ingest(JobId, logPath, _workspace.RunsRoot));

        StringAssert.Contains(exception.Message, "conflicting");
        var aggregateAfter = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(aggregateBefore.LogicalEventCount, aggregateAfter.LogicalEventCount);
        Assert.AreEqual(aggregateBefore.CostTotal, aggregateAfter.CostTotal);
        Assert.AreEqual(aggregateBefore.Version, aggregateAfter.Version);
        Assert.AreEqual(
            recordsBefore,
            ledger.ListRecords(first.GenerationId.Value).Count);
        Assert.AreEqual(
            first.CommittedOffset,
            ledger.ListGenerations(JobId).Single().CommittedOffset);
    }

    [TestMethod]
    public void Ingest_Truncation_RetainsCommittedAggregateAndCoverage()
    {
        var logPath = _workspace.LogPath("job_b2_truncate");
        var firstLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.25m,
            part: "truncate-1",
            message: "msg-truncate-1");
        var secondLine = TestWorkspace.StepStart(
            1_800_000_002_000,
            part: "truncate-2",
            message: "msg-truncate-2");
        File.WriteAllText(logPath, firstLine + "\n" + secondLine + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        var aggregateBefore = ledger.GetJobAggregate(JobId);

        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));
        var truncated = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.ContinuityUncertain, truncated.Status);
        var aggregateAfter = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(aggregateBefore.LogicalEventCount, aggregateAfter.LogicalEventCount);
        Assert.AreEqual(aggregateBefore.CostTotal, aggregateAfter.CostTotal);
        Assert.AreEqual(1, ledger.ListGenerations(JobId).Count);
    }

    [TestMethod]
    public void ListCommittedLogicalEvents_IsBoundedDeterministicAndAppliedOnly()
    {
        var logPath = _workspace.LogPath("job_b2_b3_seam");
        var lines = new string[5];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepFinish(
                1_800_000_001_000 + index,
                0.01m,
                part: "seam-" + index.ToString("000"),
                message: "msg-seam-" + index.ToString("000"));
        }

        lines[2] = lines[1];
        _workspace.WriteLog("job_b2_b3_seam", lines);

        using var ledger = CreateLedger();
        ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        var page = ledger.ListCommittedLogicalEvents(JobId, null, 2);
        Assert.AreEqual(2, page.Count);
        Assert.IsTrue(page[0].LogicalEventId < page[1].LogicalEventId);
        var next = ledger.ListCommittedLogicalEvents(JobId, page[^1].LogicalEventId, 2);
        Assert.AreEqual(2, next.Count);
        Assert.AreEqual(4, ledger.GetJobAggregate(JobId).LogicalEventCount);
    }

    [TestMethod]
    public void Ingest_ControlCharacterInSourceIds_DoesNotCollide()
    {
        var logPath = _workspace.LogPath("job_b2_control_ids");
        var timestamp = 1_800_000_001_000L;
        var first = RawStepStart(timestamp, "ses_control", "b\u001Fc", "a");
        var second = RawStepStart(timestamp, "ses_control", "c", "a\u001Fb");
        File.WriteAllText(
            logPath,
            first + "\n" + second + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(2, result.AcceptedEvents.Count);
        Assert.AreEqual(2, ledger.GetJobAggregate(JobId).LogicalEventCount);
    }

    [TestMethod]
    public void Ingest_MissingSessionMarkerLiteral_DoesNotCollideWithMissingSession()
    {
        var logPath = _workspace.LogPath("job_b2_missing_session_marker");
        var timestamp = 1_800_000_001_000L;
        var first = RawStepStart(
            timestamp,
            "__missing_session__",
            "marker-part",
            "marker-message");
        var second = RawStepStart(
            timestamp,
            session: null,
            "marker-part",
            "marker-message");
        File.WriteAllText(
            logPath,
            first + "\n" + second + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(2, result.AcceptedEvents.Count);
        Assert.AreEqual(2, ledger.GetJobAggregate(JobId).LogicalEventCount);
    }

    [TestMethod]
    public void Ingest_DecimalCostScaleDifference_IsDuplicateNotConflict()
    {
        var logPath = _workspace.LogPath("job_b2_decimal_scale");
        var timestamp = 1_800_000_001_000L;
        var first = RawStepFinish(timestamp, "0.50");
        var second = RawStepFinish(timestamp, "0.5");
        File.WriteAllText(
            logPath,
            first + "\n" + second + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(1, result.AcceptedEvents.Count);
        Assert.AreEqual(1, ledger.GetJobAggregate(JobId).LogicalEventCount);
        Assert.AreEqual(0.50m, ledger.GetJobAggregate(JobId).CostTotal);
    }

    [TestMethod]
    public void RegisterJob_ConflictingAssociation_FailsClosedWithoutIngestMutation()
    {
        var logPath = _workspace.LogPath("job_b2_conflicting_job");
        _workspace.WriteLog(
            "job_b2_conflicting_job",
            TestWorkspace.StepStart(1_800_000_000_000));
        var otherRoot = Path.Combine(_workspace.Root, "other-runs");

        using var ledger = CreateLedger();
        Assert.IsFalse(ledger.RegisterJob(
            JobId,
            _workspace.RunsRoot,
            Path.Combine(_workspace.RunsRoot, "a.json")));
        Assert.IsTrue(ledger.RegisterJob(
            JobId,
            otherRoot,
            Path.Combine(otherRoot, "a.json")));

        var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.IdentityConflict, ingest.Status);
        Assert.AreEqual(0, ledger.ListGenerations(JobId).Count);
        Assert.AreEqual(0, ledger.GetJobAggregate(JobId).LogicalEventCount);
        Assert.AreEqual(LedgerCoverageState.Conflict, ledger.GetJobCoverage(JobId).State);
    }

    [TestMethod]
    public void TimelineSnapshot_RestoresBothWindowsAndAmbiguityPolicy()
    {
        const int capacity = 500;
        var logPath = _workspace.LogPath("job_b2_dual_window");
        var lines = new List<string>
        {
            TestWorkspace.StepStart(
                1_800_000_900_000,
                part: "dual-high",
                message: "dual-high-msg")
        };
        for (var index = 1; index <= 502; index++)
        {
            lines.Add(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "dual-" + index.ToString("0000"),
                message: "dual-msg-" + index.ToString("0000")));
        }

        File.WriteAllText(
            logPath,
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger(new EventLedgerOptions
        {
            MaxBytesPerTransaction = 1024 * 1024
        });
        ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        var snapshot = ledger.ListTimelineSnapshot(JobId, capacity);
        Assert.AreEqual(capacity, snapshot.SourceWindow.Count);
        Assert.AreEqual(capacity, snapshot.NewestTimestampedWindow.Count);
        Assert.IsFalse(snapshot.SourceWindow.Any(item =>
            item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
        Assert.IsTrue(snapshot.NewestTimestampedWindow.Any(item =>
            item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
        Assert.IsFalse(snapshot.HasObservedAmbiguousTimestamps);

        var restored = new TimelineRetentionBuffer(capacity);
        restored.Restore(
            snapshot.SourceWindow,
            snapshot.NewestTimestampedWindow,
            snapshot.HasObservedAmbiguousTimestamps);
        restored.AddRange(new[]
        {
            new MonitoringEvent
            {
                EventKey = "missing-timestamp",
                JobId = JobId,
                ObservedAt = DateTimeOffset.UtcNow,
                Summary = "missing timestamp",
                Timestamp = null
            }
        });

        var retained = restored.RetainSourceOrder();
        Assert.AreEqual(capacity, retained.Count);
        Assert.IsFalse(retained.Any(item =>
            item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
    }

    private static string RawStepStart(
        long timestamp,
        string? session,
        string part,
        string message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "step_start",
            ["timestamp"] = timestamp,
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = part,
                ["messageID"] = message,
                ["type"] = "step-start"
            }
        };
        if (session is not null)
        {
            payload["sessionID"] = session;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string RawStepFinish(long timestamp, string costLiteral) =>
        $"{{\"type\":\"step_finish\",\"timestamp\":{timestamp},\"sessionID\":\"ses_decimal\",\"part\":{{\"id\":\"decimal-part\",\"messageID\":\"decimal-message\",\"type\":\"step-finish\",\"cost\":{costLiteral}}}}}";

    private EventLedger CreateLedger(EventLedgerOptions? options = null) =>
        EventLedger.OpenProjection(_databasePath, _workspace.Root, options);

    private void IngestAll(EventLedger ledger, string jobId, string logPath)
    {
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var result = ledger.Ingest(jobId, logPath, _workspace.RunsRoot);
            if (result.Status == LedgerIngestStatus.NoData)
            {
                return;
            }

            Assert.IsTrue(
                result.Status == LedgerIngestStatus.Committed,
                $"Unexpected ingest status {result.Status}: {string.Join(" | ", result.Warnings)}");
            if (result.Bootstrap is { IsComplete: true } &&
                result.CommittedOffset >= new FileInfo(logPath).Length)
            {
                return;
            }
        }

        Assert.Fail("Bounded ingestion did not converge.");
    }
}
