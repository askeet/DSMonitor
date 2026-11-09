using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DSMonitor.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// B1 crash-recovery verification. These tests intentionally kill a retained
/// test-owned child process after it has physically flushed dirty SQLite pages.
/// The first test interrupts raw SQL outside the real Ingest path; the second
/// uses a separate rollback-journal fixture and lets SQLite itself recover the
/// hot journal in a later test-only raw writable phase. Neither test claims the
/// production ledger recovers rollback journals automatically.
/// </summary>
[TestClass]
public sealed class EventLedgerCrashRecoveryTests
{
    private const string JobId = "job_ledger_crash_0001";

    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public async Task Wal_InterruptedRawTransaction_PreservesCommittedStateAndReopensCleanly()
    {
        var databasePath = Path.Combine(_workspace.Root, "wal-crash", "events.db");
        var logPath = Path.Combine(_workspace.RunsRoot, "job_ledger_wal_crash.log");
        var firstLine = TestWorkspace.StepStart(
            1_800_000_000_000,
            part: "wal-crash-1",
            message: "wal-crash-1");
        var secondLine = TestWorkspace.Text(
            1_800_000_001_000,
            "wal crash committed ingest",
            part: "wal-crash-2");
        var thirdLine = TestWorkspace.Text(
            1_800_000_002_000,
            "wal crash resumed line",
            part: "wal-crash-3");

        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));
        var baseline = CreateBaselineLedger(databasePath, logPath);

        File.AppendAllText(logPath, secondLine + "\n", new UTF8Encoding(false));
        var expectedCursorAfterChild = new FileInfo(logPath).Length;
        var preChildDatabase = File.ReadAllBytes(databasePath);

        using var child = CrashHostChild.Start(
            "wal",
            _workspace.Root,
            databasePath,
            _workspace.Root,
            JobId,
            logPath,
            _workspace.RunsRoot);
        await child.WaitForReadyAsync(TimeSpan.FromSeconds(30));
        child.KillOwnedChild();

        var walPath = databasePath + "-wal";
        Assert.IsTrue(File.Exists(walPath), "The child did not leave a WAL: " + child.Describe());

        // Inspect the WAL before any SQLite connection is opened. A commit
        // frame from the real child ingest must be followed by frames from the
        // raw, uncommitted transaction.
        var wal = InspectWal(File.ReadAllBytes(walPath));
        Assert.IsTrue(wal.FrameCount > 0, "The WAL contains no frames.");
        Assert.IsTrue(wal.LastCommitFrameIndex >= 0, "The WAL contains no commit frame.");
        Assert.IsTrue(
            wal.LastCommitFrameIndex < wal.FrameCount - 1,
            $"Expected trailing uncommitted frames after commit frame {wal.LastCommitFrameIndex} " +
            $"in a WAL with {wal.FrameCount} frames.");

        // The committed ingest and raw transaction stayed in the WAL; the main
        // database itself must still be the pre-child physical baseline.
        CollectionAssert.AreEqual(
            preChildDatabase,
            File.ReadAllBytes(databasePath),
            "The main database changed before the interrupted transaction was resolved.");

        long finalCursor;
        long finalVersion;
        using (var reopened = new EventLedger(databasePath, _workspace.Root))
        {
            var generation = reopened.ListGenerations(JobId).Single();
            Assert.AreEqual(baseline.GenerationId, generation.GenerationId);
            Assert.AreEqual(expectedCursorAfterChild, generation.CommittedOffset);
            Assert.AreEqual(baseline.Version + 1, generation.CursorVersion);

            var records = reopened.ListRecords(generation.GenerationId);
            var events = reopened.ListEvents(generation.GenerationId);
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(2, events.Count);
            Assert.IsFalse(
                records.Any(record => string.Equals(
                    record.ContentSha256,
                    "speculative-crash-row",
                    StringComparison.Ordinal)),
                "A speculative raw record survived WAL recovery.");
            Assert.IsFalse(
                events.Any(item => string.Equals(
                    item.Summary,
                    "speculative crash row",
                    StringComparison.Ordinal)),
                "A speculative raw event survived WAL recovery.");

            // Resume at the exact committed byte cursor, once and only once.
            File.AppendAllText(logPath, thirdLine + "\n", new UTF8Encoding(false));
            finalCursor = new FileInfo(logPath).Length;
            var next = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, next.Status);
            Assert.AreEqual(1, next.RecordsCommitted);
            Assert.AreEqual(1, next.EventsCommitted);
            Assert.AreEqual(finalCursor, next.CommittedOffset);

            var repeated = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.NoData, repeated.Status);
            Assert.AreEqual(0, repeated.RecordsCommitted);
            Assert.AreEqual(0, repeated.EventsCommitted);
            Assert.AreEqual(finalCursor, repeated.CommittedOffset);

            var finalGeneration = reopened.ListGenerations(JobId).Single();
            Assert.AreEqual(finalCursor, finalGeneration.CommittedOffset);
            finalVersion = finalGeneration.CursorVersion;
            Assert.AreEqual(baseline.Version + 2, finalVersion);
            Assert.AreEqual(3, reopened.ListRecords(generation.GenerationId).Count);
            Assert.AreEqual(3, reopened.ListEvents(generation.GenerationId).Count);
        }

        using var verify = OpenRaw(databasePath);
        Assert.AreEqual("ok", ScalarText(verify, "PRAGMA integrity_check;"));
        AssertNoForeignKeyViolations(verify);
        Assert.AreEqual(3L, ScalarInt64(verify, "SELECT COUNT(*) FROM source_records;"));
        Assert.AreEqual(3L, ScalarInt64(verify, "SELECT COUNT(*) FROM source_events;"));
        Assert.AreEqual(
            finalCursor,
            ScalarInt64(verify, "SELECT committed_offset FROM source_cursors LIMIT 1;"));
        Assert.AreEqual(
            finalVersion,
            ScalarInt64(verify, "SELECT version FROM source_cursors LIMIT 1;"));
    }

    [TestMethod]
    public async Task RollbackHotJournal_ReadOnlyPreflightRefuses_ThenSqliteRecoversBaseline()
    {
        var databasePath = Path.Combine(_workspace.Root, "journal-crash", "events.db");
        var logPath = Path.Combine(_workspace.RunsRoot, "job_ledger_journal_crash.log");
        var baselinePath = databasePath + ".pre-transaction";
        var firstLine = TestWorkspace.StepStart(
            1_800_000_000_000,
            part: "journal-crash-1",
            message: "journal-crash-1");
        var secondLine = TestWorkspace.Text(
            1_800_000_001_000,
            "journal crash resumed line",
            part: "journal-crash-2");

        File.WriteAllText(logPath, firstLine + "\n", new UTF8Encoding(false));
        var baseline = CreateBaselineLedger(databasePath, logPath);

        using var child = CrashHostChild.Start(
            "journal",
            _workspace.Root,
            databasePath,
            _workspace.Root,
            JobId,
            baselinePath);
        await child.WaitForReadyAsync(TimeSpan.FromSeconds(30));
        child.KillOwnedChild();

        var journalPath = databasePath + "-journal";
        Assert.IsTrue(
            File.Exists(journalPath),
            "The child did not leave a rollback journal: " + child.Describe());

        var journal = File.ReadAllBytes(journalPath);
        ValidateRollbackJournal(journal);

        Assert.IsTrue(File.Exists(baselinePath), "The child did not record its pre-transaction baseline.");
        var cleanDatabase = File.ReadAllBytes(baselinePath);
        CollectionAssert.AreNotEqual(
            cleanDatabase,
            File.ReadAllBytes(databasePath),
            "The main database was not physically dirtied by the uncommitted spill.");

        // The production ledger must fail closed in its read-only preflight and
        // must not modify the database or the hot journal. This test records
        // the real result; it does not change production behavior.
        var databaseBeforePreflight = File.ReadAllBytes(databasePath);
        var journalBeforePreflight = File.ReadAllBytes(journalPath);
        LedgerException? refusal = null;
        try
        {
            using var probe = new EventLedger(databasePath, _workspace.Root);
        }
        catch (LedgerException ex)
        {
            refusal = ex;
        }

        Assert.IsNotNull(
            refusal,
            "EventLedger unexpectedly opened a hot rollback journal during read-only preflight. " +
            "The approved architecture states this must fail closed; no production change was made.");
        Assert.IsTrue(
            refusal.Failure is LedgerStorageFailure.Unavailable
                or LedgerStorageFailure.Corrupt
                or LedgerStorageFailure.Locked,
            $"Unexpected preflight failure '{refusal.Failure}'.");
        CollectionAssert.AreEqual(
            databaseBeforePreflight,
            File.ReadAllBytes(databasePath),
            "The failed ledger preflight modified the main database.");
        CollectionAssert.AreEqual(
            journalBeforePreflight,
            File.ReadAllBytes(journalPath),
            "The failed ledger preflight modified the hot journal.");

        // Explicitly separate test-only raw writable phase. SQLite itself sees
        // the hot journal and performs rollback recovery; the test does not
        // edit, truncate, or delete the journal manually.
        using (var raw = OpenRaw(databasePath))
        {
            Assert.AreEqual("ok", ScalarText(raw, "PRAGMA integrity_check;"));
            AssertNoForeignKeyViolations(raw);
            AssertBaselineState(raw, baseline);
        }

        Assert.IsFalse(
            File.Exists(journalPath),
            "SQLite should remove the hot journal after its own rollback recovery.");

        using (var reopened = new EventLedger(databasePath, _workspace.Root))
        {
            var generation = reopened.ListGenerations(JobId).Single();
            Assert.AreEqual(baseline.GenerationId, generation.GenerationId);
            Assert.AreEqual(baseline.Offset, generation.CommittedOffset);
            Assert.AreEqual(baseline.Version, generation.CursorVersion);
            Assert.AreEqual(1, reopened.ListRecords(generation.GenerationId).Count);
            Assert.AreEqual(1, reopened.ListEvents(generation.GenerationId).Count);

            File.AppendAllText(logPath, secondLine + "\n", new UTF8Encoding(false));
            var next = reopened.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, next.Status);
            Assert.AreEqual(1, next.RecordsCommitted);
            Assert.AreEqual(1, next.EventsCommitted);
            Assert.AreEqual(new FileInfo(logPath).Length, next.CommittedOffset);
            Assert.AreEqual(
                LedgerIngestStatus.NoData,
                reopened.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
            Assert.AreEqual(2, reopened.ListRecords(generation.GenerationId).Count);
            Assert.AreEqual(2, reopened.ListEvents(generation.GenerationId).Count);
        }

        using var verify = OpenRaw(databasePath);
        Assert.AreEqual("ok", ScalarText(verify, "PRAGMA integrity_check;"));
        AssertNoForeignKeyViolations(verify);
    }

    private (long GenerationId, long Offset, long Version) CreateBaselineLedger(
        string databasePath,
        string logPath)
    {
        using var ledger = new EventLedger(databasePath, _workspace.Root);
        var ingest = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(LedgerIngestStatus.Committed, ingest.Status);
        Assert.IsNotNull(ingest.GenerationId);
        var generation = ledger.ListGenerations(JobId).Single();
        return (ingest.GenerationId.Value, generation.CommittedOffset, generation.CursorVersion);
    }

    private static (int FrameCount, int LastCommitFrameIndex) InspectWal(byte[] wal)
    {
        Assert.IsTrue(wal.Length >= 32, "The WAL is shorter than its 32-byte header.");
        var magic = ReadBigEndianUInt32(wal, 0);
        Assert.IsTrue(
            magic is 0x377f0682u or 0x377f0683u,
            $"Invalid WAL magic 0x{magic:X8}.");
        var bigEndianWords = magic == 0x377f0683u;

        Assert.AreEqual(
            3007000u,
            ReadBigEndianUInt32(wal, 4),
            "Unexpected WAL file-format version.");
        var pageSize = ReadBigEndianUInt32(wal, 8);
        Assert.IsTrue(
            pageSize is >= 512u and <= 65536u
                && (pageSize & (pageSize - 1)) == 0
                && pageSize % 8 == 0,
            $"Invalid WAL page size {pageSize}.");

        var salt1 = ReadBigEndianUInt32(wal, 16);
        var salt2 = ReadBigEndianUInt32(wal, 20);
        var headerChecksum1 = ReadBigEndianUInt32(wal, 24);
        var headerChecksum2 = ReadBigEndianUInt32(wal, 28);
        var (computedHeaderChecksum1, computedHeaderChecksum2) =
            ComputeChecksum(wal, 0, 24, bigEndianWords, 0, 0);
        Assert.AreEqual(headerChecksum1, computedHeaderChecksum1, "Invalid WAL header checksum-1.");
        Assert.AreEqual(headerChecksum2, computedHeaderChecksum2, "Invalid WAL header checksum-2.");

        var frameSize = checked((int)pageSize + 24);
        Assert.AreEqual(
            0,
            (wal.Length - 32) % frameSize,
            "The WAL ends with a partial frame.");

        var frameCount = 0;
        var lastCommitFrameIndex = -1;
        var checksum1 = computedHeaderChecksum1;
        var checksum2 = computedHeaderChecksum2;
        for (var offset = 32; offset + frameSize <= wal.Length; offset += frameSize)
        {
            var pageNumber = ReadBigEndianUInt32(wal, offset);
            var databaseSize = ReadBigEndianUInt32(wal, offset + 4);
            Assert.AreNotEqual(0u, pageNumber, $"Frame {frameCount} has page number zero.");
            Assert.AreEqual(salt1, ReadBigEndianUInt32(wal, offset + 8), $"Frame {frameCount} salt-1.");
            Assert.AreEqual(salt2, ReadBigEndianUInt32(wal, offset + 12), $"Frame {frameCount} salt-2.");

            (checksum1, checksum2) = ComputeChecksum(
                wal,
                offset,
                8,
                bigEndianWords,
                checksum1,
                checksum2);
            (checksum1, checksum2) = ComputeChecksum(
                wal,
                offset + 24,
                checked((int)pageSize),
                bigEndianWords,
                checksum1,
                checksum2);
            Assert.AreEqual(
                ReadBigEndianUInt32(wal, offset + 16),
                checksum1,
                $"Frame {frameCount} checksum-1.");
            Assert.AreEqual(
                ReadBigEndianUInt32(wal, offset + 20),
                checksum2,
                $"Frame {frameCount} checksum-2.");

            if (databaseSize != 0)
            {
                lastCommitFrameIndex = frameCount;
            }

            frameCount++;
        }

        return (frameCount, lastCommitFrameIndex);
    }

    private static void ValidateRollbackJournal(byte[] journal)
    {
        Assert.IsTrue(journal.Length > 512, $"The journal is too short ({journal.Length} bytes).");
        ReadOnlySpan<byte> magic = stackalloc byte[]
        {
            0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7
        };
        Assert.IsTrue(
            journal.AsSpan(0, 8).SequenceEqual(magic),
            "The rollback journal has an invalid header magic.");

        var pageCount = ReadBigEndianUInt32(journal, 8);
        _ = ReadBigEndianUInt32(journal, 12); // checksum nonce
        var initialDatabaseSize = ReadBigEndianUInt32(journal, 16);
        var sectorSize = ReadBigEndianUInt32(journal, 20);
        var pageSize = ReadBigEndianUInt32(journal, 24);

        Assert.IsTrue(
            pageSize is >= 512u and <= 65536u && (pageSize & (pageSize - 1)) == 0,
            $"Invalid journal page size {pageSize}.");
        Assert.IsTrue(
            sectorSize is >= 512u and <= 65536u && (sectorSize & (sectorSize - 1)) == 0,
            $"Invalid journal sector size {sectorSize}.");
        Assert.IsTrue(sectorSize <= pageSize, "The journal sector size exceeds its page size.");
        Assert.AreEqual(0u, pageSize % sectorSize, "The journal page size is not sector aligned.");
        Assert.IsTrue(initialDatabaseSize > 0, "The journal reports an empty initial database.");
        Assert.IsTrue(
            pageCount == uint.MaxValue || pageCount <= (journal.Length / (pageSize + 4)) + 1,
            $"The journal page count {pageCount} is not sane for its length.");
        Assert.IsTrue(
            journal.Length >= (long)sectorSize + pageSize + 4,
            "The journal contains no complete page record.");
    }

    private static (uint Checksum1, uint Checksum2) ComputeChecksum(
        byte[] data,
        int offset,
        int length,
        bool bigEndianWords,
        uint checksum1,
        uint checksum2)
    {
        Assert.AreEqual(0, length % 8, "SQLite checksum input must be a multiple of eight bytes.");
        for (var index = 0; index < length; index += 8)
        {
            var first = bigEndianWords
                ? ReadBigEndianUInt32(data, offset + index)
                : ReadLittleEndianUInt32(data, offset + index);
            var second = bigEndianWords
                ? ReadBigEndianUInt32(data, offset + index + 4)
                : ReadLittleEndianUInt32(data, offset + index + 4);

            // SQLite's WAL checksum updates the two accumulators as a pair:
            //   s0 += x[0] + s1; s1 += x[1] + s0;
            checksum1 += first + checksum2;
            checksum2 += second + checksum1;
        }

        return (checksum1, checksum2);
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];

    private static uint ReadLittleEndianUInt32(byte[] data, int offset) =>
        ((uint)data[offset + 3] << 24)
        | ((uint)data[offset + 2] << 16)
        | ((uint)data[offset + 1] << 8)
        | data[offset];

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AssertNoForeignKeyViolations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.IsFalse(reader.Read(), "PRAGMA foreign_key_check reported a violation.");
    }

    private static void AssertBaselineState(
        SqliteConnection connection,
        (long GenerationId, long Offset, long Version) baseline)
    {
        Assert.AreEqual(
            1L,
            ScalarInt64(
                connection,
                $"SELECT COUNT(*) FROM source_records WHERE generation_id = {baseline.GenerationId};"));
        Assert.AreEqual(
            1L,
            ScalarInt64(
                connection,
                "SELECT COUNT(*) FROM source_events;"));
        Assert.AreEqual(
            baseline.Offset,
            ScalarInt64(
                connection,
                $"SELECT committed_offset FROM source_cursors WHERE generation_id = {baseline.GenerationId};"));
        Assert.AreEqual(
            baseline.Version,
            ScalarInt64(
                connection,
                $"SELECT version FROM source_cursors WHERE generation_id = {baseline.GenerationId};"));
        Assert.AreEqual(
            0L,
            ScalarInt64(
                connection,
                "SELECT COUNT(*) FROM source_records WHERE content_sha256 = 'speculative-crash-row';"));
        Assert.AreEqual(
            0L,
            ScalarInt64(
                connection,
                "SELECT COUNT(*) FROM source_events WHERE summary = 'speculative crash row';"));
    }

    private sealed class CrashHostChild : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource<bool> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();

        private CrashHostChild(Process process) => _process = process;

        public static CrashHostChild Start(
            string mode,
            string workingDirectory,
            params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotNetHost(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add(typeof(EventLedgerCrashRecoveryTests).Assembly.Location);
            startInfo.ArgumentList.Add(EventLedgerCrashRecoveryHost.Command);
            startInfo.ArgumentList.Add(mode);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var child = new CrashHostChild(process);
            process.OutputDataReceived += child.OnOutput;
            process.ErrorDataReceived += child.OnError;
            if (!process.Start())
            {
                process.Dispose();
                throw new AssertFailedException("The crash host process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        public async Task WaitForReadyAsync(TimeSpan timeout)
        {
            try
            {
                await _ready.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new AssertFailedException(
                    $"The crash host did not signal READY within {timeout}. {Describe()}");
            }

            if (_process.HasExited)
            {
                throw new AssertFailedException(
                    $"The crash host exited before the parent could kill it. {Describe()}");
            }
        }

        public void KillOwnedChild()
        {
            if (!_process.HasExited)
            {
                try
                {
                    // Only the exact retained test-owned child is terminated;
                    // no unrelated process is searched for or touched.
                    _process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill; WaitForExit
                    // below still establishes the final state.
                }
            }

            if (!_process.WaitForExit(15_000))
            {
                throw new AssertFailedException(
                    $"The crash host did not terminate after Kill. {Describe()}");
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                var exited = _process.HasExited;
                return $"pid={_process.Id}, exited={exited}, " +
                    $"stdout='{_stdout.ToString().Trim()}', stderr='{_stderr.ToString().Trim()}'";
            }
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }

            _process.WaitForExit(15_000);
            _process.Dispose();
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
            {
                return;
            }

            lock (_gate)
            {
                _stdout.AppendLine(args.Data);
            }

            if (string.Equals(args.Data, "READY", StringComparison.Ordinal))
            {
                _ready.TrySetResult(true);
            }
        }

        private void OnError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
            {
                return;
            }

            lock (_gate)
            {
                _stderr.AppendLine(args.Data);
            }
        }

        private static string ResolveDotNetHost()
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDirectory))
            {
                var runtimeHost = Path.GetFullPath(
                    Path.Combine(runtimeDirectory, "..", "..", "..", "dotnet.exe"));
                if (File.Exists(runtimeHost))
                {
                    return runtimeHost;
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var standardHost = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            return File.Exists(standardHost) ? standardHost : "dotnet";
        }
    }
}
