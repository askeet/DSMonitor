using System.Data;
using System.Text;
using System.Text.Json;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class EventLedgerTests
{
    private const string JobId = "job_ledger_0001";

    private TestWorkspace _workspace = null!;
    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _databasePath = Path.Combine(_workspace.Root, "data", "events.db");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Ingest_ReopenAndReprocess_DoesNotDuplicateOrResetCursor()
    {
        var logPath = LogPath("job_ledger_reopen.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000) + "\n",
            new UTF8Encoding(false));

        long firstCursor;
        using (var ledger = CreateLedger())
        {
            var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
            Assert.AreEqual(1, first.RecordsCommitted);
            Assert.AreEqual(1, first.EventsCommitted);
            Assert.IsNotNull(first.GenerationId);
            firstCursor = first.CommittedOffset;

            var duplicate = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.NoData, duplicate.Status);
            Assert.AreEqual(0, duplicate.RecordsCommitted);
            Assert.AreEqual(first.GenerationId, duplicate.GenerationId);
        }

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "after restart", part: "p2") + "\n",
            new UTF8Encoding(false));

        using (var reopened = CreateLedger())
        {
            var generations = reopened.ListGenerations(JobId);
            Assert.AreEqual(1, generations.Count);
            Assert.AreEqual(firstCursor, generations[0].CommittedOffset);

            var next = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, next.Status);
            Assert.AreEqual(1, next.RecordsCommitted);
            Assert.AreEqual(1, next.EventsCommitted);
            Assert.AreEqual(generations[0].GenerationId, next.GenerationId);

            var records = reopened.ListRecords(next.GenerationId!.Value);
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(new FileInfo(logPath).Length, next.CommittedOffset);
            Assert.AreEqual(LedgerIngestStatus.NoData,
                reopened.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }
    }

    [TestMethod]
    public void Ingest_IdenticalBytesAtDifferentOffsets_AreDistinctRecords()
    {
        var line = TestWorkspace.StepStart(
            1_800_000_000_000,
            part: "identical",
            message: "identical");
        var logPath = LogPath("job_ledger_identical.log");
        File.WriteAllText(logPath, line + "\n" + line + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(2, result.RecordsCommitted);
        Assert.AreEqual(2, result.EventsCommitted);

        var records = ledger.ListRecords(result.GenerationId!.Value);
        Assert.AreEqual(2, records.Count);
        Assert.AreNotEqual(records[0].StartByte, records[1].StartByte);
        Assert.AreEqual(records[0].ContentSha256, records[1].ContentSha256);
        Assert.AreEqual(LedgerRecordDisposition.Accepted, records[0].Disposition);
        Assert.AreEqual(LedgerRecordDisposition.Accepted, records[1].Disposition);
    }

    [TestMethod]
    public void Ingest_SameBytesInDifferentGenerations_ArePreserved()
    {
        var line = TestWorkspace.StepStart(1_800_000_000_000, part: "copy", message: "copy");
        var logPath = LogPath("job_ledger_original.log");
        var copyPath = LogPath("job_ledger_copy.log");
        File.WriteAllText(logPath, line + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var original = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, original.Status);

        File.Copy(logPath, copyPath);
        var copy = ledger.Ingest(JobId, copyPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, copy.Status);
        Assert.AreNotEqual(original.GenerationId, copy.GenerationId);

        var generations = ledger.ListGenerations(JobId);
        Assert.AreEqual(2, generations.Count);
        var originalRecords = ledger.ListRecords(original.GenerationId!.Value);
        var copyRecords = ledger.ListRecords(copy.GenerationId!.Value);
        Assert.AreEqual(1, originalRecords.Count);
        Assert.AreEqual(1, copyRecords.Count);
        Assert.AreEqual(originalRecords[0].ContentSha256, copyRecords[0].ContentSha256);
        Assert.AreEqual(0, copyRecords[0].StartByte);
    }

    [TestMethod]
    public void Ingest_ReplacementAtSamePath_CreatesNewGenerationFromZero()
    {
        var firstLine = TestWorkspace.StepStart(1_800_000_000_000, part: "replace-1", message: "replace-1");
        var secondLine = TestWorkspace.Text(1_800_000_001_000, "replacement", part: "replace-2");
        var logPath = LogPath("job_ledger_replace.log");
        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var original = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, original.Status);

        var replacement = Path.Combine(_workspace.RunsRoot, "replacement.tmp");
        File.WriteAllText(
            replacement,
            firstLine + "\n" + secondLine + "\n",
            new UTF8Encoding(false));
        File.Move(replacement, logPath, overwrite: true);

        var replaced = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, replaced.Status);
        Assert.AreNotEqual(original.GenerationId, replaced.GenerationId);
        Assert.AreEqual(2, replaced.RecordsCommitted);
        Assert.AreEqual(new FileInfo(logPath).Length, replaced.CommittedOffset);

        var generations = ledger.ListGenerations(JobId);
        Assert.AreEqual(2, generations.Count);
        Assert.AreEqual(1, ledger.ListRecords(original.GenerationId!.Value).Count);
        Assert.AreEqual(2, ledger.ListRecords(replaced.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Ingest_RenameSamePhysicalFile_ContinuesGenerationAndUpdatesPath()
    {
        var logPath = LogPath("job_ledger_rename.log");
        var renamedPath = LogPath("job_ledger_renamed.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "rename-1", message: "rename-1") + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        File.Move(logPath, renamedPath);
        var afterRename = ledger.Ingest(JobId, renamedPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.NoData, afterRename.Status);
        Assert.AreEqual(first.GenerationId, afterRename.GenerationId);
        var generation = ledger.ListGenerations(JobId).Single();
        Assert.AreEqual(Path.GetFullPath(renamedPath), generation.SourcePath);

        File.AppendAllText(
            renamedPath,
            TestWorkspace.Text(1_800_000_001_000, "renamed append", part: "rename-2") + "\n",
            new UTF8Encoding(false));

        var appended = ledger.Ingest(JobId, renamedPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, appended.Status);
        Assert.AreEqual(first.GenerationId, appended.GenerationId);
        Assert.AreEqual(1, appended.RecordsCommitted);
        Assert.AreEqual(2, ledger.ListRecords(appended.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Ingest_TruncationBelowCursor_FailsClosedWithoutMutation()
    {
        var logPath = LogPath("job_ledger_truncate.log");
        var firstLine = TestWorkspace.StepStart(1_800_000_000_000, part: "truncate-1", message: "truncate-1");
        var secondLine = TestWorkspace.Text(1_800_000_001_000, "truncate", part: "truncate-2");
        File.WriteAllText(logPath, firstLine + "\n" + secondLine + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        var committedOffset = first.CommittedOffset;
        var recordCount = ledger.ListRecords(first.GenerationId!.Value).Count;

        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));

        var truncated = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.ContinuityUncertain, truncated.Status);
        Assert.AreEqual(first.GenerationId, truncated.GenerationId);
        var generation = ledger.ListGenerations(JobId).Single();
        Assert.AreEqual(committedOffset, generation.CommittedOffset);
        Assert.AreEqual(recordCount, ledger.ListRecords(first.GenerationId.Value).Count);
    }

    [TestMethod]
    public void Ingest_PartialTrailingLine_DoesNotAdvanceCursor()
    {
        var logPath = LogPath("job_ledger_partial.log");
        var firstLine = TestWorkspace.StepStart(1_800_000_000_000, part: "partial-1", message: "partial-1");
        var secondLine = TestWorkspace.Text(1_800_000_001_000, "partial", part: "partial-2");
        var firstBytes = Encoding.UTF8.GetBytes(firstLine + "\n");
        File.WriteAllBytes(logPath, firstBytes);

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        Assert.AreEqual(firstBytes.Length, first.CommittedOffset);

        var partial = Encoding.UTF8.GetBytes(secondLine);
        using (var append = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            append.Write(partial, 0, partial.Length);
        }

        var pending = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.NoData, pending.Status);
        Assert.AreEqual(firstBytes.Length, pending.CommittedOffset);
        Assert.AreEqual(1, ledger.ListRecords(first.GenerationId!.Value).Count);

        File.AppendAllText(logPath, "\n", new UTF8Encoding(false));
        var completed = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, completed.Status);
        Assert.AreEqual(1, completed.RecordsCommitted);
        Assert.AreEqual(new FileInfo(logPath).Length, completed.CommittedOffset);
        Assert.AreEqual(2, ledger.ListRecords(completed.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Ingest_MultibyteRecordAcrossBoundedWindow_IsNotCommittedUntilComplete()
    {
        var logPath = LogPath("job_ledger_multibyte.log");
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "évent",
            ["timestamp"] = 1_800_000_000_000,
            ["sessionID"] = "ses_multibyte",
            ["part"] = new Dictionary<string, object?>
            {
                ["id"] = "part-multibyte",
                ["type"] = "évent",
                ["text"] = new string('x', 200)
            }
        });
        File.WriteAllText(logPath, line + "\n", new UTF8Encoding(false));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(line) > 64);

        using (var small = CreateLedger(new EventLedgerOptions
               {
                   MaxBytesPerTransaction = 64,
                   MaxLineBytes = 4096
               }))
        {
            var tooLarge = small.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.RecordTooLarge, tooLarge.Status);
            Assert.AreEqual(0, small.ListGenerations(JobId).Count);
        }

        using var complete = CreateLedger(new EventLedgerOptions
        {
            MaxBytesPerTransaction = 4096,
            MaxLineBytes = 4096
        });
        var result = complete.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(1, result.EventsCommitted);
        var events = complete.ListEvents(result.GenerationId!.Value);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("Event: évent", events[0].Summary);
    }

    [TestMethod]
    public void Ingest_RealMultibyteBytesSplitAcrossPolls_DecodeAfterCompletion()
    {
        var logPath = LogPath("job_ledger_multibyte_split.log");
        var line =
            "{\"type\":\"日本語\",\"timestamp\":1800000000000,\"sessionID\":\"ses_split\"," +
            "\"part\":{\"id\":\"part-split\",\"type\":\"日本語\",\"text\":\"payload\"}}";
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var typeIndex = line.IndexOf("日本語", StringComparison.Ordinal);
        Assert.IsTrue(typeIndex >= 0);
        var split = Encoding.UTF8.GetByteCount(line[..typeIndex]) + 1;
        Assert.IsTrue(split > 0 && split < bytes.Length);
        Assert.IsTrue((bytes[split] & 0xC0) == 0x80, "split must fall inside a UTF-8 continuation byte");

        File.WriteAllBytes(logPath, bytes[..split]);

        using var ledger = CreateLedger();
        var partial = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.IsTrue(partial.IsSuccess);
        Assert.AreEqual(0, partial.RecordsCommitted);
        Assert.AreEqual(0, partial.EventsCommitted);
        Assert.AreEqual(0, partial.CommittedOffset);
        var partialGeneration = ledger.ListGenerations(JobId).Single();
        Assert.AreEqual(0, partialGeneration.CommittedOffset);
        Assert.AreEqual(0, ledger.ListRecords(partialGeneration.GenerationId).Count);

        using (var append = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            append.Write(bytes, split, bytes.Length - split);
        }

        var completed = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, completed.Status);
        Assert.AreEqual(1, completed.RecordsCommitted);
        Assert.AreEqual(1, completed.EventsCommitted);
        Assert.AreEqual(bytes.Length, completed.CommittedOffset);
        var events = ledger.ListEvents(completed.GenerationId!.Value);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("Event: 日本語", events[0].Summary);
    }

    [TestMethod]
    public void Ingest_InvalidUtf8_IsRecordedWithoutAnEvent()
    {
        var logPath = LogPath("job_ledger_invalid_utf8.log");
        File.WriteAllBytes(logPath, new byte[] { 0xFF, 0xFE, (byte)'\n' });

        using var ledger = CreateLedger();
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(1, result.RecordsCommitted);
        Assert.AreEqual(0, result.EventsCommitted);
        var record = ledger.ListRecords(result.GenerationId!.Value).Single();
        Assert.AreEqual(LedgerRecordDisposition.InvalidUtf8, record.Disposition);
        Assert.AreEqual(2, record.PayloadLength);
        Assert.AreEqual(0, ledger.ListEvents(result.GenerationId.Value).Count);
    }

    [TestMethod]
    public void Ingest_MalformedJsonAndOverlong_AreRecordedWithoutEvents()
    {
        var logPath = LogPath("job_ledger_dispositions.log");
        File.WriteAllText(logPath, "not-json\n" + new string('x', 200) + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger(new EventLedgerOptions
        {
            MaxLineBytes = 32,
            MaxBytesPerTransaction = 4096
        });
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
        Assert.AreEqual(2, result.RecordsCommitted);
        Assert.AreEqual(0, result.EventsCommitted);
        var records = ledger.ListRecords(result.GenerationId!.Value);
        Assert.AreEqual(LedgerRecordDisposition.MalformedJson, records[0].Disposition);
        Assert.AreEqual(LedgerRecordDisposition.Overlong, records[1].Disposition);
        Assert.AreEqual(new FileInfo(logPath).Length, result.CommittedOffset);
        Assert.AreEqual(0, ledger.ListEvents(result.GenerationId.Value).Count);
    }

    [TestMethod]
    public void Ingest_ReplayedCommittedRange_ValidatesEndAndDoesNotDuplicate()
    {
        var logPath = LogPath("job_ledger_replay_range.log");
        var originalLine = TestWorkspace.StepStart(
            1_800_000_000_000,
            part: "range-original",
            message: "range-original");
        File.WriteAllText(logPath, originalLine + "\n", new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        ResetCursorToZero();
        var replay = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, replay.Status);
        Assert.AreEqual(0, replay.RecordsCommitted);
        Assert.AreEqual(0, replay.EventsCommitted);
        Assert.AreEqual(1, ledger.ListRecords(first.GenerationId!.Value).Count);
        Assert.AreEqual(1, ledger.ListEvents(first.GenerationId.Value).Count);

        ResetCursorToZero();
        File.WriteAllText(
            logPath,
            TestWorkspace.Text(
                1_800_000_002_000,
                "different content with a different range",
                part: "range-different") + "\n",
            new UTF8Encoding(false));

        try
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.Fail("A conflicting range at the same start must fail closed.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.ConstraintViolation, ex.Failure);
        }

        Assert.AreEqual(1, ledger.ListRecords(first.GenerationId.Value).Count);
        Assert.AreEqual(0, ledger.ListGenerations(JobId).Single().CommittedOffset);
    }

    private void ResetCursorToZero()
    {
        using var raw = OpenRawConnection();
        using var command = raw.CreateCommand();
        command.CommandText = "UPDATE source_cursors SET committed_offset = 0;";
        command.ExecuteNonQuery();
    }

    [TestMethod]
    public void Retrieval_ListRecordsAndEvents_AreBoundedAndDeterministic()
    {
        const int eventCount = 650;
        var logPath = LogPath("job_ledger_paging.log");
        var builder = new StringBuilder();
        for (var index = 0; index < eventCount; index++)
        {
            builder.Append(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "paging-" + index.ToString("0000"),
                message: "paging-" + index.ToString("0000")));
            builder.Append('\n');
        }

        File.WriteAllText(logPath, builder.ToString());

        using var ledger = CreateLedger();
        var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, ingest.Status);
        var generationId = ingest.GenerationId!.Value;

        var defaultRecords = ledger.ListRecords(generationId);
        Assert.AreEqual(EventLedger.DefaultReadLimit, defaultRecords.Count);
        Assert.AreEqual(0, defaultRecords[0].StartByte);
        Assert.IsTrue(defaultRecords.Zip(defaultRecords.Skip(1))
            .All(pair => pair.First.StartByte < pair.Second.StartByte));

        var records = new List<LedgerRecord>();
        long? afterStart = null;
        while (true)
        {
            var page = ledger.ListRecords(generationId, afterStart, 100);
            if (page.Count == 0)
            {
                break;
            }

            records.AddRange(page);
            afterStart = page[^1].StartByte;
        }

        Assert.AreEqual(eventCount, records.Count);
        Assert.IsTrue(records.Zip(records.Skip(1))
            .All(pair => pair.First.StartByte < pair.Second.StartByte));

        var defaultEvents = ledger.ListEvents(generationId);
        Assert.AreEqual(EventLedger.DefaultReadLimit, defaultEvents.Count);

        var events = new List<LedgerEventMetadata>();
        long? afterEventId = null;
        while (true)
        {
            var page = ledger.ListEvents(generationId, afterEventId, 100);
            if (page.Count == 0)
            {
                break;
            }

            events.AddRange(page);
            afterEventId = page[^1].EventId;
        }

        Assert.AreEqual(eventCount, events.Count);
        Assert.IsTrue(events.Zip(events.Skip(1))
            .All(pair => pair.First.EventId < pair.Second.EventId));

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => ledger.ListRecords(generationId, null, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => ledger.ListEvents(generationId, null, EventLedger.MaxReadLimit + 1));
    }

    [TestMethod]
    public void Ingest_AmbiguousIdentity_FailsClosedWithoutGeneration()
    {
        var logPath = LogPath("job_ledger_weak_identity.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        using var ledger = CreateLedger(identityReader: _ => default);
        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.AmbiguousSourceIdentity, result.Status);
        Assert.IsNull(result.GenerationId);
        Assert.AreEqual(0, ledger.ListGenerations(JobId).Count);
    }

    [TestMethod]
    public void Ingest_PathIdentityChangesDuringRead_FailsClosedWithoutMutation()
    {
        var logPath = LogPath("job_ledger_identity_change.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        var calls = 0;
        using var ledger = CreateLedger(identityReader: _ =>
        {
            calls++;
            return calls == 1
                ? new LedgerSourceIdentity(1, 1, 1)
                : new LedgerSourceIdentity(2, 2, 2);
        });

        var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.ContinuityUncertain, result.Status);
        Assert.IsNull(result.GenerationId);
        Assert.AreEqual(0, ledger.ListGenerations(JobId).Count);
    }

    [TestMethod]
    public void Ingest_MissingSource_ReturnsUnavailableWithoutGeneration()
    {
        using var ledger = CreateLedger();
        var result = ledger.Ingest(
            JobId,
            Path.Combine(_workspace.RunsRoot, "missing.log"),
            _workspace.RunsRoot);

        Assert.AreEqual(LedgerIngestStatus.SourceUnavailable, result.Status);
        Assert.AreEqual(0, ledger.ListGenerations(JobId).Count);
    }

    [TestMethod]
    public void Storage_UnsupportedSchema_FailsClosedAndPreservesData()
    {
        var logPath = LogPath("job_ledger_schema.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        using (var ledger = CreateLedger())
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                ledger.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        using (var raw = OpenRawConnection())
        using (var command = raw.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(_databasePath);
        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("An unsupported schema must not be opened.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(_databasePath));

        using var verify = OpenRawConnection();
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM source_records;";
        Assert.AreEqual(1L, Convert.ToInt64(verifyCommand.ExecuteScalar()));
    }

    [TestMethod]
    public void Schema_UnexpectedTrigger_IsRejectedOnOpenAndPreservesData()
    {
        var logPath = LogPath("job_ledger_trigger_open.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        long committedOffset;
        long generationId;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, ingest.Status);
            committedOffset = ingest.CommittedOffset;
            generationId = ingest.GenerationId!.Value;
        }

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TRIGGER silently_drop_events
                BEFORE INSERT ON source_events
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("An unexpected trigger must reject the ledger schema.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        using var verify = OpenRawConnection();
        using var recordCount = verify.CreateCommand();
        recordCount.CommandText = "SELECT COUNT(*) FROM source_records;";
        Assert.AreEqual(1L, Convert.ToInt64(recordCount.ExecuteScalar()));
        using var eventCount = verify.CreateCommand();
        eventCount.CommandText = "SELECT COUNT(*) FROM source_events;";
        Assert.AreEqual(1L, Convert.ToInt64(eventCount.ExecuteScalar()));
        using var cursor = verify.CreateCommand();
        cursor.CommandText =
            "SELECT committed_offset FROM source_cursors WHERE generation_id = $generation;";
        cursor.Parameters.AddWithValue("$generation", generationId);
        Assert.AreEqual(committedOffset, Convert.ToInt64(cursor.ExecuteScalar()));
    }

    [TestMethod]
    public void Schema_MalformedTable_IsRejectedOnOpen()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using (var raw = OpenRawConnection())
        using (var command = raw.CreateCommand())
        {
            command.CommandText =
                """
                PRAGMA foreign_keys = OFF;
                DROP TABLE source_events;
                CREATE TABLE source_events (event_id INTEGER PRIMARY KEY);
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A malformed ledger table must be rejected.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }
    }

    [TestMethod]
    public void Schema_ConstraintDeficientTable_IsRejectedOnOpen()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using (var raw = OpenRawConnection())
        using (var command = raw.CreateCommand())
        {
            command.CommandText =
                """
                PRAGMA foreign_keys = OFF;
                DROP TABLE source_records;
                CREATE TABLE source_records (
                    record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    generation_id INTEGER NOT NULL,
                    start_byte INTEGER NOT NULL,
                    end_byte INTEGER NOT NULL,
                    payload_length INTEGER NOT NULL,
                    disposition TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    committed_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A constraint-deficient ledger table must be rejected.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }
    }

    [TestMethod]
    public void Ingest_TriggerAddedAfterOpen_RollsBackOnZeroAffectedRows()
    {
        var logPath = LogPath("job_ledger_trigger_runtime.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "trigger-1", message: "trigger-1") + "\n");

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "trigger", part: "trigger-2") + "\n");

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TRIGGER silently_drop_events
                BEFORE INSERT ON source_events
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.Fail("A silently ignored event insert must roll back the batch.");
        }
        catch (LedgerException ex)
        {
            // The stronger in-transaction schema validation now rejects the
            // unexpected trigger before any mutation. The integrity assertions
            // below still verify that no partial batch was committed.
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        var generation = ledger.ListGenerations(JobId).Single();
        Assert.AreEqual(first.CommittedOffset, generation.CommittedOffset);
        Assert.AreEqual(1, ledger.ListRecords(first.GenerationId!.Value).Count);
        Assert.AreEqual(1, ledger.ListEvents(first.GenerationId.Value).Count);

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText = "DROP TRIGGER silently_drop_events;";
            trigger.ExecuteNonQuery();
        }

        var retried = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, retried.Status);
        Assert.AreEqual(1, retried.RecordsCommitted);
        Assert.AreEqual(2, ledger.ListEvents(retried.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Storage_CorruptFile_FailsClosedAndIsNotDeleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var bytes = Encoding.UTF8.GetBytes("this is not a sqlite database");
        File.WriteAllBytes(_databasePath, bytes);

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A corrupt ledger must not be reinitialized.");
        }
        catch (LedgerException ex)
        {
            Assert.IsTrue(
                ex.Failure is LedgerStorageFailure.Corrupt or LedgerStorageFailure.Unavailable,
                ex.Failure.ToString());
        }

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(_databasePath));
    }

    [TestMethod]
    public void Storage_LockedDatabase_FailsClosedAndLeavesCursorUnchanged()
    {
        var logPath = LogPath("job_ledger_locked.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "lock-1", message: "lock-1") + "\n");

        using var firstLedger = CreateLedger();
        var first = firstLedger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        using var secondLedger = CreateLedger(new EventLedgerOptions
        {
            BusyTimeoutMilliseconds = 150
        });

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "locked append", part: "lock-2") + "\n");

        using var lockConnection = OpenRawConnection();
        using (var begin = lockConnection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        try
        {
            try
            {
                secondLedger.Ingest(JobId, logPath, _workspace.RunsRoot);
                Assert.Fail("A locked database must fail closed.");
            }
            catch (LedgerException ex)
            {
                Assert.AreEqual(LedgerStorageFailure.Locked, ex.Failure);
            }

            var generation = firstLedger.ListGenerations(JobId).Single();
            Assert.AreEqual(first.CommittedOffset, generation.CommittedOffset);
            Assert.AreEqual(1, firstLedger.ListRecords(first.GenerationId!.Value).Count);
        }
        finally
        {
            using var rollback = lockConnection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        var retried = firstLedger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, retried.Status);
        Assert.AreEqual(2, firstLedger.ListRecords(retried.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Storage_ParentJunction_IsRejectedBeforeDatabaseCreation()
    {
        var allowedRoot = Path.Combine(_workspace.Root, "allowed");
        var outsideTarget = Path.Combine(_workspace.Root, "outside-target");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsideTarget);
        var junction = Path.Combine(allowedRoot, "link");
        _workspace.CreateJunction(junction, outsideTarget);

        var redirectedDatabase = Path.Combine(junction, "events.db");
        try
        {
            using var ignored = new EventLedger(redirectedDatabase, allowedRoot);
            Assert.Fail("A parent junction must not be followed.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        Assert.IsFalse(File.Exists(Path.Combine(outsideTarget, "events.db")));
    }

    [TestMethod]
    public void Storage_SidecarJunction_IsRejectedBeforeSqliteOpen()
    {
        var logPath = LogPath("job_ledger_sidecar_link.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        using (var ledger = CreateLedger())
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                ledger.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var outsideTarget = Path.Combine(_workspace.Root, "sidecar-target");
        Directory.CreateDirectory(outsideTarget);
        var walPath = _databasePath + "-wal";
        var shmPath = _databasePath + "-shm";
        foreach (var sidecar in new[] { walPath, shmPath })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        _workspace.CreateJunction(walPath, outsideTarget);

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A reparse-point SQLite sidecar must not be followed.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        Assert.IsFalse(File.Exists(Path.Combine(outsideTarget, "events.db-wal")));
        Assert.IsFalse(File.Exists(Path.Combine(outsideTarget, "events.db-shm")));
    }

    [TestMethod]
    public void Storage_SidecarsStayInsideApprovedRoot()
    {
        var logPath = LogPath("job_ledger_sidecar_location.log");
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        using var ledger = CreateLedger();
        Assert.AreEqual(
            LedgerIngestStatus.Committed,
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot).Status);

        var sidecars = new[] { _databasePath + "-wal", _databasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        Assert.IsTrue(sidecars.Length > 0, "SQLite WAL/sidecar files were expected after a write.");
        foreach (var sidecar in sidecars)
        {
            Assert.IsTrue(PathSafety.IsWithinRoot(sidecar, _workspace.Root));
            var attributes = File.GetAttributes(sidecar);
            Assert.AreEqual(0, (int)(attributes & FileAttributes.ReparsePoint));
        }
    }

    [TestMethod]
    public void Storage_UnavailablePath_FailsClosedAndPreservesExistingFiles()
    {
        var blocker = Path.Combine(_workspace.Root, "blocked-parent");
        File.WriteAllText(blocker, "keep me");
        var unavailableDatabase = Path.Combine(blocker, "events.db");

        try
        {
            using var ignored = new EventLedger(unavailableDatabase, _workspace.Root);
            Assert.Fail("An unavailable storage path must not be initialized.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        Assert.AreEqual("keep me", File.ReadAllText(blocker));
    }

    [TestMethod]
    public void Storage_RejectsDatabaseOutsideApprovedRoot()
    {
        var outsideDatabase = Path.Combine(_workspace.Root, "unapproved", "events.db");

        var exception = Assert.ThrowsException<ArgumentException>(
            () => new EventLedger(outsideDatabase, _workspace.RunsRoot));

        StringAssert.Contains(exception.Message, "approved");
    }

    [TestMethod]
    public void Atomic_NewGenerationFailure_RollsBackGenerationRecordsEventsAndCursor()
    {
        var logPath = LogPath("job_ledger_atomic_new.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "atomic-1", message: "atomic-1") + "\n" +
            TestWorkspace.Text(1_800_000_001_000, "atomic", part: "atomic-2") + "\n");

        using var ledger = CreateLedger();
        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TRIGGER fail_event_insert
                BEFORE INSERT ON source_events
                BEGIN
                    SELECT RAISE(ABORT, 'forced ledger failure');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.Fail("The forced event failure must abort the transaction.");
        }
        catch (LedgerException ex)
        {
            // The unexpected trigger is rejected by the in-transaction schema
            // check before mutation; the assertions below verify that the
            // generation, records, events, and cursor were not created.
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        Assert.AreEqual(0, ledger.ListGenerations(JobId).Count);

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText = "DROP TRIGGER fail_event_insert;";
            trigger.ExecuteNonQuery();
        }

        var retried = ledger.Ingest(
            JobId,
            logPath,
            _workspace.RunsRoot,
            new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(LedgerIngestStatus.Committed, retried.Status);
        Assert.AreEqual(2, retried.RecordsCommitted);
        Assert.AreEqual(2, ledger.ListEvents(retried.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Atomic_CursorUpdateFailure_RollsBackRecordsEventsAndCursor()
    {
        var logPath = LogPath("job_ledger_atomic_existing.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "cursor-1", message: "cursor-1") + "\n");

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "cursor", part: "cursor-2") + "\n");

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TRIGGER fail_cursor_update
                BEFORE UPDATE ON source_cursors
                BEGIN
                    SELECT RAISE(ABORT, 'forced cursor failure');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.Fail("The forced cursor failure must abort the transaction.");
        }
        catch (LedgerException ex)
        {
            // The unexpected trigger is rejected by the in-transaction schema
            // check before mutation; the assertions below verify cursor and
            // record/event preservation.
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        var generation = ledger.ListGenerations(JobId).Single();
        Assert.AreEqual(first.CommittedOffset, generation.CommittedOffset);
        Assert.AreEqual(1, ledger.ListRecords(first.GenerationId!.Value).Count);
        Assert.AreEqual(1, ledger.ListEvents(first.GenerationId.Value).Count);

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText = "DROP TRIGGER fail_cursor_update;";
            trigger.ExecuteNonQuery();
        }

        var retried = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, retried.Status);
        Assert.AreEqual(2, ledger.ListRecords(retried.GenerationId!.Value).Count);
    }

    [TestMethod]
    public async Task Ingest_ConcurrentLedgers_DoNotDuplicateOrReuseCursor()
    {
        var logPath = LogPath("job_ledger_concurrent.log");
        var builder = new StringBuilder();
        for (var index = 0; index < 100; index++)
        {
            builder.Append(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "concurrent-" + index.ToString("000"),
                message: "concurrent-" + index.ToString("000")));
            builder.Append('\n');
        }

        File.WriteAllText(logPath, builder.ToString());

        var options = new EventLedgerOptions { BusyTimeoutMilliseconds = 10_000 };
        using var first = CreateLedger(options);
        using var second = CreateLedger(options);

        var results = await Task.WhenAll(
            Task.Run(() => first.Ingest(JobId, logPath, _workspace.RunsRoot)),
            Task.Run(() => second.Ingest(JobId, logPath, _workspace.RunsRoot)));

        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.IsTrue(results.Any(result => result.Status == LedgerIngestStatus.Committed));

        var generations = first.ListGenerations(JobId);
        Assert.AreEqual(1, generations.Count);
        Assert.AreEqual(new FileInfo(logPath).Length, generations[0].CommittedOffset);
        Assert.AreEqual(100, first.ListRecords(generations[0].GenerationId).Count);
        Assert.AreEqual(100, first.ListEvents(generations[0].GenerationId).Count);
    }

    [TestMethod]
    public void Schema_SqliteWildcardTrigger_IsRejectedOnOpenAndPreservesState()
    {
        var logPath = LogPath("job_ledger_sqlite_trigger_open.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "sqlite-trigger-1") + "\n",
            new UTF8Encoding(false));

        long committedOffset;
        long generationId;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, ingest.Status);
            committedOffset = ingest.CommittedOffset;
            generationId = ingest.GenerationId!.Value;
        }

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            // This name was omitted by the old LIKE 'sqlite_%' filter because
            // '_' is a SQLite wildcard matching the 'X'.
            trigger.CommandText =
                """
                CREATE TRIGGER sqliteXdrop_events
                AFTER INSERT ON source_events
                BEGIN
                    DELETE FROM source_events WHERE event_id = NEW.event_id;
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(_databasePath);
        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("The sqliteX-prefixed trigger must be rejected by the exact allowlist.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(_databasePath));
        AssertPersistedState(generationId, committedOffset, 1, 1);
    }

    [TestMethod]
    public void Schema_SqliteWildcardAfterTriggerAddedAfterOpen_IsRejectedBeforeMutation()
    {
        var logPath = LogPath("job_ledger_sqlite_trigger_runtime.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "sqlite-trigger-2") + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        var first = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "after trigger", part: "sqlite-trigger-3") + "\n",
            new UTF8Encoding(false));

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText =
                """
                CREATE TRIGGER sqliteXdrop_events
                AFTER INSERT ON source_events
                BEGIN
                    DELETE FROM source_events WHERE event_id = NEW.event_id;
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.Fail("The trigger must be rejected inside the immediate write transaction.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        AssertPersistedState(first.GenerationId!.Value, first.CommittedOffset, 1, 1);

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText = "DROP TRIGGER sqliteXdrop_events;";
            trigger.ExecuteNonQuery();
        }

        var retried = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, retried.Status);
        Assert.AreEqual(1, retried.RecordsCommitted);
        Assert.AreEqual(1, retried.EventsCommitted);
        AssertPersistedState(first.GenerationId.Value, retried.CommittedOffset, 2, 2);
    }

    [TestMethod]
    public void Schema_QuotedWildcardTrigger_IsRejectedOnOpenAndDatabaseReopensAfterRemoval()
    {
        var logPath = LogPath("job_ledger_quoted_trigger.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "quoted-trigger-1") + "\n",
            new UTF8Encoding(false));

        long committedOffset;
        long generationId;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            committedOffset = ingest.CommittedOffset;
            generationId = ingest.GenerationId!.Value;
        }

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            // A quoted identifier containing an embedded quote is still stored
            // as sqliteXdrop"events and was also hidden by the old wildcard.
            trigger.CommandText =
                """
                CREATE TRIGGER "sqliteXdrop""events"
                AFTER INSERT ON source_events
                BEGIN
                    DELETE FROM source_events WHERE event_id = NEW.event_id;
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        using (var raw = OpenRawConnection())
        using (var name = raw.CreateCommand())
        {
            name.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger';";
            Assert.AreEqual("sqliteXdrop\"events", Convert.ToString(name.ExecuteScalar()));
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A quoted sqliteX-prefixed trigger must be rejected.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        AssertPersistedState(generationId, committedOffset, 1, 1);

        using (var raw = OpenRawConnection())
        using (var trigger = raw.CreateCommand())
        {
            trigger.CommandText = "DROP TRIGGER \"sqliteXdrop\"\"events\";";
            trigger.ExecuteNonQuery();
        }

        using var reopened = CreateLedger();
        Assert.AreEqual(1, reopened.ListRecords(generationId).Count);
        Assert.AreEqual(1, reopened.ListEvents(generationId).Count);
    }

    [TestMethod]
    public void Schema_UnexpectedViewOrIndex_IsRejectedAndPreservesData()
    {
        var logPath = LogPath("job_ledger_extra_objects.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "extra-object-1") + "\n",
            new UTF8Encoding(false));

        long committedOffset;
        long generationId;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            committedOffset = ingest.CommittedOffset;
            generationId = ingest.GenerationId!.Value;
        }

        using (var raw = OpenRawConnection())
        using (var view = raw.CreateCommand())
        {
            view.CommandText = "CREATE VIEW sqliteXview AS SELECT 1 AS value;";
            view.ExecuteNonQuery();
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("An unexpected view must reject the ledger schema.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        using (var raw = OpenRawConnection())
        using (var view = raw.CreateCommand())
        {
            view.CommandText = "DROP VIEW sqliteXview;";
            view.ExecuteNonQuery();
        }

        using (var raw = OpenRawConnection())
        using (var index = raw.CreateCommand())
        {
            index.CommandText = "CREATE INDEX sqliteXextra_index ON jobs(created_utc);";
            index.ExecuteNonQuery();
        }

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("An unexpected index must reject the ledger schema.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.IncompatibleSchema, ex.Failure);
        }

        using (var raw = OpenRawConnection())
        using (var index = raw.CreateCommand())
        {
            index.CommandText = "DROP INDEX sqliteXextra_index;";
            index.ExecuteNonQuery();
        }

        AssertPersistedState(generationId, committedOffset, 1, 1);
        using var reopened = CreateLedger();
        Assert.AreEqual(1, reopened.ListRecords(generationId).Count);
    }

    [TestMethod]
    public void Postconditions_AfterTriggerEventDeletion_AreDetectedAndRolledBack()
    {
        var logPath = LogPath("job_ledger_postcondition.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "postcondition-1") + "\n",
            new UTF8Encoding(false));

        long generationId;
        long committedOffset;
        long cursorVersion;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, ingest.Status);
            generationId = ingest.GenerationId!.Value;
            var generation = ledger.ListGenerations(JobId).Single();
            committedOffset = generation.CommittedOffset;
            cursorVersion = generation.CursorVersion;
        }

        using var raw = OpenRawConnection();
        using var transaction = raw.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        using (var trigger = raw.CreateCommand())
        {
            trigger.Transaction = transaction;
            trigger.CommandText =
                """
                CREATE TRIGGER sqliteXdrop_events
                AFTER INSERT ON source_events
                BEGIN
                    DELETE FROM source_events WHERE event_id = NEW.event_id;
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        using (var insertRecord = raw.CreateCommand())
        {
            insertRecord.Transaction = transaction;
            insertRecord.CommandText =
                """
                INSERT INTO source_records(
                    generation_id, start_byte, end_byte, payload_length,
                    disposition, content_sha256, committed_utc)
                VALUES ($generation, 100, 102, 1, 'Accepted', 'test', '2026-11-09T00:00:00.0000000+00:00');
                """;
            insertRecord.Parameters.AddWithValue("$generation", generationId);
            Assert.AreEqual(1, insertRecord.ExecuteNonQuery());
        }

        long recordId;
        using (var lastId = raw.CreateCommand())
        {
            lastId.Transaction = transaction;
            lastId.CommandText = "SELECT last_insert_rowid();";
            recordId = Convert.ToInt64(lastId.ExecuteScalar());
        }

        using (var insertEvent = raw.CreateCommand())
        {
            insertEvent.Transaction = transaction;
            insertEvent.CommandText =
                """
                INSERT INTO source_events(
                    record_id, job_id, event_key, session_id, event_timestamp_utc,
                    observed_at_utc, category, severity, summary)
                VALUES (
                    $record, $job, 'postcondition-key', NULL, NULL,
                    '2026-11-09T00:00:00.0000000+00:00', 'Unknown', 'Info', 'summary');
                """;
            insertEvent.Parameters.AddWithValue("$record", recordId);
            insertEvent.Parameters.AddWithValue("$job", JobId);
            Assert.AreEqual(1, insertEvent.ExecuteNonQuery());
        }

        using (var eventCount = raw.CreateCommand())
        {
            eventCount.Transaction = transaction;
            eventCount.CommandText =
                "SELECT COUNT(*) FROM source_events WHERE record_id = $record;";
            eventCount.Parameters.AddWithValue("$record", recordId);
            Assert.AreEqual(0L, Convert.ToInt64(eventCount.ExecuteScalar()));
        }

        var exception = Assert.ThrowsException<LedgerException>(
            () => EventLedger.ValidateCommittedState(
                raw,
                transaction,
                JobId,
                generationId,
                committedOffset,
                cursorVersion,
                rangeStart: 100,
                rangeEnd: 102,
                expectedRecords: 1,
                expectedEvents: 1));
        Assert.AreEqual(LedgerStorageFailure.ConstraintViolation, exception.Failure);

        transaction.Rollback();

        using var verify = OpenRawConnection();
        using var verifyRecords = verify.CreateCommand();
        verifyRecords.CommandText =
            "SELECT COUNT(*) FROM source_records WHERE generation_id = $generation;";
        verifyRecords.Parameters.AddWithValue("$generation", generationId);
        Assert.AreEqual(1L, Convert.ToInt64(verifyRecords.ExecuteScalar()));
    }

    [TestMethod]
    public void Storage_AncestorJunctionAboveApprovedRoot_IsRejectedBeforeCreation()
    {
        var outsideTarget = Path.Combine(_workspace.Root, "ancestor-target");
        Directory.CreateDirectory(outsideTarget);
        var junction = Path.Combine(_workspace.Root, "ancestor-junction");
        _workspace.CreateJunction(junction, outsideTarget);

        var approvedRoot = Path.Combine(junction, "approved");
        var redirectedDatabase = Path.Combine(approvedRoot, "data", "events.db");
        try
        {
            using var ignored = new EventLedger(redirectedDatabase, approvedRoot);
            Assert.Fail("An ancestor junction above the approved root must not be followed.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        Assert.IsFalse(Directory.Exists(Path.Combine(outsideTarget, "approved")));
        Assert.IsFalse(File.Exists(Path.Combine(outsideTarget, "approved", "data", "events.db")));
    }

    [TestMethod]
    public void Storage_ApprovedRootJunction_IsRejectedBeforeDatabaseCreation()
    {
        var outsideTarget = Path.Combine(_workspace.Root, "root-target");
        Directory.CreateDirectory(outsideTarget);
        var rootJunction = Path.Combine(_workspace.Root, "root-junction");
        _workspace.CreateJunction(rootJunction, outsideTarget);

        var redirectedDatabase = Path.Combine(rootJunction, "events.db");
        try
        {
            using var ignored = new EventLedger(redirectedDatabase, rootJunction);
            Assert.Fail("A reparse-point approved root must not be followed.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        Assert.IsFalse(File.Exists(Path.Combine(outsideTarget, "events.db")));
    }

    [TestMethod]
    public void Storage_OrphanSidecarWithoutDatabase_IsRejectedWithoutModification()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var walPath = _databasePath + "-wal";
        var bytes = Encoding.UTF8.GetBytes("orphan sidecar fixture");
        File.WriteAllBytes(walPath, bytes);

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A sidecar without its database must not be trusted.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(walPath));
        Assert.IsFalse(File.Exists(_databasePath));
    }

    [TestMethod]
    public void Storage_HardLinkedDatabase_IsRejectedWithoutModifyingEitherName()
    {
        var logPath = LogPath("job_ledger_hardlink_db.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "hardlink-db-1") + "\n",
            new UTF8Encoding(false));

        using (var ledger = CreateLedger())
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                ledger.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var before = File.ReadAllBytes(_databasePath);
        var hardLink = Path.Combine(_workspace.Root, "events-hardlink.db");
        Assert.IsTrue(
            ReparsePointFixture.TryCreateHardLink(hardLink, _databasePath),
            "The test environment could not create a hard link.");

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A multiply-linked database must be rejected.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(_databasePath));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(hardLink));

        File.Delete(hardLink);
        using var reopened = CreateLedger();
        Assert.IsTrue(File.Exists(reopened.DatabasePath));
    }

    [TestMethod]
    public void Storage_HardLinkedWalAndShmSidecars_AreRejectedWithoutModification()
    {
        var logPath = LogPath("job_ledger_hardlink_sidecars.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "hardlink-sidecar-1") + "\n",
            new UTF8Encoding(false));

        using var ledger = CreateLedger();
        Assert.AreEqual(
            LedgerIngestStatus.Committed,
            ledger.Ingest(JobId, logPath, _workspace.RunsRoot).Status);

        var walPath = _databasePath + "-wal";
        var shmPath = _databasePath + "-shm";
        Assert.IsTrue(File.Exists(walPath), "A WAL sidecar was expected after a write.");
        Assert.IsTrue(File.Exists(shmPath), "An SHM sidecar was expected after a write.");

        foreach (var sidecar in new[] { walPath, shmPath })
        {
            var hardLink = sidecar + ".hardlink";
            Assert.IsTrue(
                ReparsePointFixture.TryCreateHardLink(hardLink, sidecar),
                "The test environment could not create a sidecar hard link.");

            var before = ReadAllBytesShared(sidecar);
            try
            {
                using var ignored = CreateLedger();
                Assert.Fail("A multiply-linked SQLite sidecar must be rejected.");
            }
            catch (LedgerException ex)
            {
                Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
            }

            CollectionAssert.AreEqual(before, ReadAllBytesShared(sidecar));
            File.Delete(hardLink);
        }

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "after link", part: "hardlink-sidecar-2") + "\n",
            new UTF8Encoding(false));
        var appended = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, appended.Status);
        Assert.AreEqual(2, ledger.ListRecords(appended.GenerationId!.Value).Count);
    }

    [TestMethod]
    public void Storage_HardLinkedRollbackJournal_IsRejectedWithoutModification()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        var journalPath = _databasePath + "-journal";
        var journalBytes = Encoding.UTF8.GetBytes("synthetic hot-journal test fixture");
        File.WriteAllBytes(journalPath, journalBytes);

        var hardLink = Path.Combine(_workspace.Root, "events-journal-hardlink");
        Assert.IsTrue(
            ReparsePointFixture.TryCreateHardLink(hardLink, journalPath),
            "The test environment could not create a journal hard link.");

        try
        {
            using var ignored = CreateLedger();
            Assert.Fail("A multiply-linked rollback journal must be rejected.");
        }
        catch (LedgerException ex)
        {
            Assert.AreEqual(LedgerStorageFailure.Unavailable, ex.Failure);
        }

        CollectionAssert.AreEqual(journalBytes, File.ReadAllBytes(journalPath));
        CollectionAssert.AreEqual(journalBytes, File.ReadAllBytes(hardLink));

        File.Delete(hardLink);
        File.Delete(journalPath);
        using var reopened = CreateLedger();
        Assert.IsTrue(File.Exists(reopened.DatabasePath));
    }

    [TestMethod]
    public void Storage_MissingSidecars_AreSupportedOnReopen()
    {
        var logPath = LogPath("job_ledger_missing_sidecars.log");
        File.WriteAllText(
            logPath,
            TestWorkspace.StepStart(1_800_000_000_000, part: "missing-sidecar-1") + "\n",
            new UTF8Encoding(false));

        long generationId;
        using (var ledger = CreateLedger())
        {
            var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            generationId = ingest.GenerationId!.Value;
        }

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = _databasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        File.AppendAllText(
            logPath,
            TestWorkspace.Text(1_800_000_001_000, "reopen", part: "missing-sidecar-2") + "\n",
            new UTF8Encoding(false));

        using var reopened = CreateLedger();
        var next = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, next.Status);
        Assert.AreEqual(generationId, next.GenerationId);
        Assert.AreEqual(2, reopened.ListRecords(generationId).Count);
        Assert.AreEqual(2, reopened.ListEvents(generationId).Count);
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void AssertPersistedState(
        long generationId,
        long expectedOffset,
        long expectedRecords,
        long expectedEvents)
    {
        using var raw = OpenRawConnection();
        using (var records = raw.CreateCommand())
        {
            records.CommandText =
                "SELECT COUNT(*) FROM source_records WHERE generation_id = $generation;";
            records.Parameters.AddWithValue("$generation", generationId);
            Assert.AreEqual(expectedRecords, Convert.ToInt64(records.ExecuteScalar()));
        }

        using (var events = raw.CreateCommand())
        {
            events.CommandText =
                """
                SELECT COUNT(*)
                FROM source_events e
                JOIN source_records r ON r.record_id = e.record_id
                WHERE r.generation_id = $generation;
                """;
            events.Parameters.AddWithValue("$generation", generationId);
            Assert.AreEqual(expectedEvents, Convert.ToInt64(events.ExecuteScalar()));
        }

        using var cursor = raw.CreateCommand();
        cursor.CommandText =
            "SELECT committed_offset FROM source_cursors WHERE generation_id = $generation;";
        cursor.Parameters.AddWithValue("$generation", generationId);
        Assert.AreEqual(expectedOffset, Convert.ToInt64(cursor.ExecuteScalar()));
    }

    private EventLedger CreateLedger(
        EventLedgerOptions? options = null,
        LedgerSourceIdentityReader? identityReader = null) =>
        new(_databasePath, _workspace.Root, options, identityReader);

    private SqliteConnection OpenRawConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private string LogPath(string fileName) =>
        Path.Combine(_workspace.RunsRoot, fileName);
}
