using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using DSMonitor.Persistence;
using Microsoft.Data.Sqlite;

namespace DSMonitor.Tests;

/// <summary>
/// Test-only crash host used by the B1 recovery verification tests. It performs
/// real SQLite work in a child process, flushes dirty pages while a write
/// transaction is still uncommitted, then blocks until the parent kills exactly
/// this process. It is never used by production code.
/// </summary>
internal static class EventLedgerCrashRecoveryHost
{
    public const string Command = "event-ledger-crash-host";

    public static int Main(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "usage: <test-assembly> event-ledger-crash-host wal|journal <database> <allowed-root> <job-id> ...");
            return 2;
        }

        try
        {
            switch (args[1])
            {
                case "wal":
                    RunWalChild(args);
                    return 0;
                case "journal":
                    RunJournalChild(args);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown crash-host mode '{args[1]}'.");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HOST-ERROR: " + ex);
            Console.Error.Flush();
            return 1;
        }
    }

    private static void RunWalChild(string[] args)
    {
        RequireArgumentCount(args, 7);
        var databasePath = Path.GetFullPath(args[2]);
        var allowedRoot = args[3];
        var jobId = args[4];
        var sourcePath = args[5];
        var sourceRoot = args[6];

        // Keep this ledger connection open through the kill. Closing the last
        // WAL connection could checkpoint the committed frames into the main
        // database, which would invalidate the parent's physical baseline.
        var ledger = new EventLedger(databasePath, allowedRoot);
        var ingest = ledger.Ingest(jobId, sourcePath, sourceRoot);
        if (ingest.Status != LedgerIngestStatus.Committed || ingest.GenerationId is null)
        {
            throw new InvalidOperationException(
                $"The WAL crash child could not commit its real ingest: {ingest.Status}.");
        }

        var connection = OpenWritable(databasePath);
        RequireJournalMode(connection, "wal");

        var walPath = databasePath + "-wal";
        var walLengthBefore = ReadSharedLength(walPath);

        // This is raw interrupted SQL deliberately outside the real Ingest
        // path. It must never be treated as an EventLedger transaction.
        var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ApplySpeculativeMutation(
            connection,
            transaction,
            jobId,
            ingest.GenerationId.Value,
            ingest.CommittedOffset + 4096);

        ForceCacheFlush(connection);
        var walLengthAfter = ReadSharedLength(walPath);
        if (walLengthAfter <= walLengthBefore)
        {
            throw new InvalidOperationException(
                "sqlite3_db_cacheflush did not append speculative frames to the WAL.");
        }

        if (connection.Handle is not { IsInvalid: false })
        {
            throw new InvalidOperationException("The raw SQLite handle became invalid.");
        }

        SignalReady();

        // WaitForParentKill never returns. The post-wait KeepAlive calls make
        // these objects live across the indefinite wait so the JIT cannot
        // collect/close the ledger, raw connection, or open transaction before
        // the parent's kill.
        WaitForParentKill();
        GC.KeepAlive(transaction);
        GC.KeepAlive(connection);
        GC.KeepAlive(ledger);
    }

    private static void RunJournalChild(string[] args)
    {
        RequireArgumentCount(args, 6);
        var databasePath = Path.GetFullPath(args[2]);
        var allowedRoot = args[3];
        var jobId = args[4];
        var baselinePath = args[5];

        // Switch this isolated fixture out of WAL explicitly and prove the
        // persistent mode before taking the pre-transaction physical baseline.
        using (var modeConnection = OpenWritable(databasePath))
        {
            var switched = Convert.ToString(
                ExecuteScalar(modeConnection, "PRAGMA journal_mode = DELETE;"),
                CultureInfo.InvariantCulture);
            if (!string.Equals(switched, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The journal child could not switch the fixture to DELETE (got '{switched}').");
            }

            var confirmed = Convert.ToString(
                ExecuteScalar(modeConnection, "PRAGMA journal_mode;"),
                CultureInfo.InvariantCulture);
            if (!string.Equals(confirmed, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The journal child could not confirm DELETE mode (got '{confirmed}').");
            }
        }

        var walPath = databasePath + "-wal";
        if (File.Exists(walPath) && ReadSharedLength(walPath) > 0)
        {
            throw new InvalidOperationException(
                "The journal fixture still has a non-empty WAL after switching to DELETE.");
        }

        File.Copy(databasePath, baselinePath, overwrite: true);

        var connection = OpenWritable(databasePath);
        RequireJournalMode(connection, "delete");

        long generationId;
        long committedOffset;
        using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT generation_id, committed_offset FROM source_cursors ORDER BY generation_id LIMIT 1;";
            using var reader = query.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("The journal fixture has no committed cursor.");
            }

            generationId = reader.GetInt64(0);
            committedOffset = reader.GetInt64(1);
        }

        var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ApplySpeculativeMutation(
            connection,
            transaction,
            jobId,
            generationId,
            committedOffset + 4096);

        ForceCacheFlush(connection);

        var journalPath = databasePath + "-journal";
        if (!File.Exists(journalPath))
        {
            throw new InvalidOperationException(
                "The rollback journal was not present after the uncommitted spill.");
        }

        var journal = ReadAllBytesShared(journalPath);
        if (journal.Length <= 512 || !HasRollbackJournalMagic(journal))
        {
            throw new InvalidOperationException(
                $"The rollback journal is not a plausible hot journal (length {journal.Length}).");
        }

        SignalReady();

        // As in the WAL branch, keep the raw connection and its uncommitted
        // transaction live across the indefinite wait until the parent kills
        // this test-owned child.
        WaitForParentKill();
        GC.KeepAlive(transaction);
        GC.KeepAlive(connection);
    }

    private static void ApplySpeculativeMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        long generationId,
        long speculativeOffset)
    {
        using (var cursor = connection.CreateCommand())
        {
            cursor.Transaction = transaction;
            cursor.CommandText =
                "UPDATE source_cursors SET committed_offset = $offset, version = 777 WHERE generation_id = $generation;";
            cursor.Parameters.AddWithValue("$offset", speculativeOffset);
            cursor.Parameters.AddWithValue("$generation", generationId);
            if (cursor.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The speculative cursor update did not affect one row.");
            }
        }

        var start = speculativeOffset + 100;
        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText =
                """
                INSERT INTO source_records(
                    generation_id, start_byte, end_byte, payload_length,
                    disposition, content_sha256, committed_utc)
                VALUES (
                    $generation, $start, $end, 3,
                    'Accepted', 'speculative-crash-row',
                    '2026-11-09T00:00:00.0000000+00:00');
                """;
            record.Parameters.AddWithValue("$generation", generationId);
            record.Parameters.AddWithValue("$start", start);
            record.Parameters.AddWithValue("$end", start + 4);
            if (record.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The speculative record insert did not affect one row.");
            }
        }

        long recordId;
        using (var lastId = connection.CreateCommand())
        {
            lastId.Transaction = transaction;
            lastId.CommandText = "SELECT last_insert_rowid();";
            recordId = Convert.ToInt64(lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText =
                """
                INSERT INTO source_events(
                    record_id, job_id, event_key, session_id, event_timestamp_utc,
                    observed_at_utc, category, severity, summary)
                VALUES (
                    $record, $job, 'speculative-crash-event', NULL, NULL,
                    '2026-11-09T00:00:00.0000000+00:00', 'Other', 'Info',
                    'speculative crash row');
                """;
            item.Parameters.AddWithValue("$record", recordId);
            item.Parameters.AddWithValue("$job", jobId);
            if (item.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The speculative event insert did not affect one row.");
            }
        }
    }

    private static void ForceCacheFlush(SqliteConnection connection)
    {
        var handle = connection.Handle
            ?? throw new InvalidOperationException("The raw SQLite handle is unavailable.");
        var rc = DbCacheFlush(handle.DangerousGetHandle());
        if (rc != 0)
        {
            throw new InvalidOperationException($"sqlite3_db_cacheflush failed with SQLite result {rc}.");
        }
    }

    private static SqliteConnection OpenWritable(string databasePath)
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

    private static void RequireJournalMode(SqliteConnection connection, string expected)
    {
        var mode = Convert.ToString(
            ExecuteScalar(connection, "PRAGMA journal_mode;"),
            CultureInfo.InvariantCulture);
        if (!string.Equals(mode, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected journal mode '{expected}' but observed '{mode}'.");
        }
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void RequireArgumentCount(string[] args, int required)
    {
        if (args.Length < required)
        {
            throw new ArgumentException(
                $"Crash-host mode '{args[1]}' requires at least {required - 1} arguments; received {args.Length - 1}.");
        }
    }

    private static long ReadSharedLength(string path)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.GetLength(handle);
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

    private static bool HasRollbackJournalMagic(byte[] journal)
    {
        if (journal.Length < 8)
        {
            return false;
        }

        ReadOnlySpan<byte> magic = stackalloc byte[]
        {
            0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7
        };
        return journal.AsSpan(0, 8).SequenceEqual(magic);
    }

    private static void SignalReady()
    {
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
    }

    private static void WaitForParentKill()
    {
        // The parent owns this child and terminates exactly this process. This
        // is an indefinite wait, not a sleep-based synchronization signal.
        using var stop = new ManualResetEventSlim(initialState: false);
        stop.Wait();
    }

    private static int DbCacheFlush(IntPtr databaseHandle)
    {
        try
        {
            return NativeMethods.sqlite3_db_cacheflush(databaseHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return FallbackDbCacheFlush.Value(databaseHandle);
        }
    }

    private static DbCacheFlushDelegate LoadFallbackDbCacheFlush()
    {
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var candidate = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            "e_sqlite3.dll");
        if (!File.Exists(candidate))
        {
            candidate = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        }

        if (!File.Exists(candidate))
        {
            throw new DllNotFoundException(
                $"The bundled SQLite native library was not found for '{runtimeIdentifier}'.");
        }

        var module = NativeLibrary.Load(candidate);
        var export = NativeLibrary.GetExport(module, "sqlite3_db_cacheflush");
        return Marshal.GetDelegateForFunctionPointer<DbCacheFlushDelegate>(export);
    }

    private static readonly Lazy<DbCacheFlushDelegate> FallbackDbCacheFlush =
        new(LoadFallbackDbCacheFlush);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DbCacheFlushDelegate(IntPtr databaseHandle);

    private static class NativeMethods
    {
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_db_cacheflush(IntPtr databaseHandle);
    }
}
