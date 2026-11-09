using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Persistence;

/// <summary>
/// Durable, byte-oriented SQLite event ledger foundation.
///
/// The ledger owns physical source generations, complete newline-framed byte
/// ranges, safe event metadata, and the committed byte cursor. It deliberately
/// does not participate in live monitoring and does not change timeline,
/// logging, or security behavior. Raw log/model/tool content is never stored.
///
/// Continuity is based on a verified physical file identity captured from the
/// opened handle (volume serial, file index, creation time), not on a path or
/// content hash. The ledger intentionally fails closed when identity or
/// continuity cannot be proved.
/// </summary>
public sealed class EventLedger : IDisposable
{
    public const int DefaultReadLimit = 500;
    public const int MaxReadLimit = 5_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly EventLedgerOptions _options;
    private readonly LedgerSourceIdentityReader _identityReader;
    private readonly LedgerStorageGuard _storageGuard;
    private SqliteConnection? _connection;
    private bool _disposed;

    private SqliteConnection Connection =>
        _connection ?? throw new ObjectDisposedException(nameof(EventLedger));

    public EventLedger(
        string databasePath,
        string? allowedRoot = null,
        EventLedgerOptions? options = null,
        LedgerSourceIdentityReader? identityReader = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A ledger database path is required.", nameof(databasePath));
        }

        _options = options ?? new EventLedgerOptions();
        _identityReader = identityReader ?? ReadWindowsIdentity;
        _databasePath = Path.GetFullPath(databasePath);
        var storageRoot = ResolveAndValidateStorageRoot(_databasePath, allowedRoot);
        _storageGuard = new LedgerStorageGuard(_databasePath, storageRoot);

        try
        {
            // Check the complete existing ancestry, reparse points, current
            // hard-link counts, and existing sidecars before SQLite gets a
            // chance to open, create, or recover any path.
            _storageGuard.PrepareAndValidate();

            // Validate an existing database through a read-only connection
            // before opening it read/write. This keeps incompatible, corrupt,
            // or unexpected databases byte-for-byte untouched in the normal
            // path. A concurrent replacement between this check and the write
            // open remains a documented residual race.
            ValidateExistingDatabaseReadOnly();

            _storageGuard.PrepareAndValidate();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, _options.EffectiveBusyTimeout / 1000)
            };

            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();

            // Connection-local settings are safe on an incompatible database;
            // the persistent WAL switch is deferred until the exact schema has
            // been validated or initialized.
            ConfigureConnectionDefaults();
            _storageGuard.PrepareAndValidate();
            ValidateOrInitializeSchema();
            ConfigureConnectionDurability();
            ValidateSchemaObjects();
            ValidateSchemaMetadata();
            _storageGuard.PrepareAndValidate();
        }
        catch (LedgerException)
        {
            _connection?.Dispose();
            throw;
        }
        catch (SqliteException ex)
        {
            _connection?.Dispose();
            throw MapSqliteException(ex, "The ledger database could not be opened safely.");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or ArgumentException)
        {
            _connection?.Dispose();
            throw new LedgerException(
                LedgerStorageFailure.Unavailable,
                "The ledger database could not be opened safely.",
                ex);
        }
    }

    public string DatabasePath => _databasePath;

    public LedgerIngestResult Ingest(
        string jobId,
        string sourcePath,
        string sourceRoot,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A source path is required.", nameof(sourcePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var opened = SafeFileAccess.OpenRead(sourcePath, sourceRoot);
        if (opened.Stream is null)
        {
            return new LedgerIngestResult(
                LedgerIngestStatus.SourceUnavailable,
                null,
                0,
                0,
                0,
                new[] { opened.Warning ?? "The source could not be read safely." });
        }

        var stream = opened.Stream;
        LedgerSourceIdentity identity;
        long length;
        try
        {
            identity = _identityReader(stream);
            length = stream.Length;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LedgerIngestResult(
                LedgerIngestStatus.SourceUnavailable,
                null,
                0,
                0,
                0,
                new[] { "The source is currently unavailable or locked." });
        }

        if (!identity.IsVerified)
        {
            return new LedgerIngestResult(
                LedgerIngestStatus.AmbiguousSourceIdentity,
                null,
                0,
                0,
                0,
                new[]
                {
                    "The source has no verified physical identity; the ledger refuses to guess a generation or cursor."
                });
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var now = observedAt ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            ThrowIfDisposed();
            _storageGuard.PrepareAndValidate();

            SqliteTransaction? transaction = null;
            try
            {
                transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

                // The transaction is an immediate write transaction. Validate
                // the complete schema now, while the write lock is held, so an
                // external SQLite writer cannot add a trigger or replace an
                // object between validation and mutation. Nothing has been
                // written at this point.
                ValidateWriteTransaction(transaction);
                EnsureJob(jobId, now, transaction);

                var matches = ReadMatchingGenerations(transaction, jobId, identity);
                if (matches.Count > 1)
                {
                    transaction.Rollback();
                    return new LedgerIngestResult(
                        LedgerIngestStatus.AmbiguousSourceIdentity,
                        null,
                        0,
                        0,
                        0,
                        new[]
                        {
                            "Multiple ledger generations share the same physical identity; continuity is ambiguous."
                        });
                }

                long generationId;
                long cursor;
                long cursorVersion;
                var created = false;

                if (matches.Count == 0)
                {
                    generationId = InsertGeneration(transaction, jobId, fullPath, identity, now);
                    cursor = 0;
                    cursorVersion = 0;
                    created = true;
                }
                else
                {
                    var existing = matches[0];
                    generationId = existing.GenerationId;
                    cursor = existing.CommittedOffset;
                    cursorVersion = existing.CursorVersion;

                    if (length < cursor)
                    {
                        transaction.Rollback();
                        return new LedgerIngestResult(
                            LedgerIngestStatus.ContinuityUncertain,
                            generationId,
                            cursor,
                            0,
                            0,
                            new[]
                            {
                                "The source is shorter than its committed cursor; truncation or in-place rewrite cannot be distinguished safely."
                            });
                    }

                    UpdateGenerationObservation(
                        transaction,
                        generationId,
                        fullPath,
                        now);
                }

                if (length == cursor)
                {
                    CommitWithPostconditions(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        cursorVersion,
                        rangeStart: null,
                        rangeEnd: null,
                        expectedRecords: 0,
                        expectedEvents: 0);
                    return new LedgerIngestResult(
                        created ? LedgerIngestStatus.Committed : LedgerIngestStatus.NoData,
                        generationId,
                        cursor,
                        0,
                        0,
                        Array.Empty<string>());
                }

                cancellationToken.ThrowIfCancellationRequested();

                var requested = checked((int)Math.Min(_options.EffectiveMaxBytes, length - cursor));
                var buffer = new byte[requested];
                var read = ReadFully(stream, cursor, buffer);
                if (read == 0)
                {
                    transaction.Rollback();
                    return new LedgerIngestResult(
                        LedgerIngestStatus.ContinuityUncertain,
                        created ? null : generationId,
                        cursor,
                        0,
                        0,
                        new[] { "No bytes could be read from the committed cursor." });
                }

                var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
                if (lastNewline < 0)
                {
                    if (read == requested && cursor + read < length)
                    {
                        transaction.Rollback();
                        return new LedgerIngestResult(
                            LedgerIngestStatus.RecordTooLarge,
                            created ? null : generationId,
                            cursor,
                            0,
                            0,
                            new[]
                            {
                                "No newline was found inside the bounded read window; the partial record was not committed."
                            });
                    }

                    // A trailing partial line is never allowed to advance the cursor.
                    if (!CurrentPathMatchesIdentity(fullPath, sourceRoot, identity))
                    {
                        transaction.Rollback();
                        return new LedgerIngestResult(
                            LedgerIngestStatus.ContinuityUncertain,
                            created ? null : generationId,
                            cursor,
                            0,
                            0,
                            new[]
                            {
                                "The source path no longer resolves to the verified physical identity; nothing was committed."
                            });
                    }

                    CommitWithPostconditions(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        cursorVersion,
                        rangeStart: null,
                        rangeEnd: null,
                        expectedRecords: 0,
                        expectedEvents: 0);
                    return new LedgerIngestResult(
                        created ? LedgerIngestStatus.Committed : LedgerIngestStatus.NoData,
                        generationId,
                        cursor,
                        0,
                        0,
                        Array.Empty<string>());
                }

                var completeLength = lastNewline + 1;
                var newCursor = cursor;
                var recordCount = 0;
                var eventCount = 0;
                var rangeRecordCount = 0;
                var rangeEventCount = 0;
                var lineStart = 0;

                while (lineStart < completeLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var newline = Array.IndexOf(
                        buffer,
                        (byte)'\n',
                        lineStart,
                        completeLength - lineStart);
                    if (newline < 0)
                    {
                        break;
                    }

                    var absoluteStart = cursor + lineStart;
                    var absoluteEnd = cursor + newline + 1;
                    var payloadLength = newline - lineStart;
                    var contentLength = payloadLength;
                    if (contentLength > 0 &&
                        buffer[lineStart + contentLength - 1] == (byte)'\r')
                    {
                        contentLength--;
                    }

                    var content = new ReadOnlySpan<byte>(buffer, lineStart, contentLength);
                    var disposition = AnalyzeRecord(
                        content,
                        jobId,
                        now,
                        out var parsed,
                        out var contentSha256,
                        _options.EffectiveMaxLine);

                    var (recordId, inserted) = InsertRecord(
                        transaction,
                        generationId,
                        absoluteStart,
                        absoluteEnd,
                        payloadLength,
                        disposition,
                        contentSha256,
                        now);

                    rangeRecordCount++;
                    if (disposition == LedgerRecordDisposition.Accepted && parsed is not null)
                    {
                        rangeEventCount++;
                    }

                    if (inserted)
                    {
                        recordCount++;
                        if (disposition == LedgerRecordDisposition.Accepted && parsed is not null)
                        {
                            InsertEvent(transaction, recordId, parsed.Event);
                            eventCount++;
                        }
                    }

                    lineStart = newline + 1;
                    newCursor = absoluteEnd;
                }

                if (stream.Length < newCursor)
                {
                    transaction.Rollback();
                    return new LedgerIngestResult(
                        LedgerIngestStatus.ContinuityUncertain,
                        generationId,
                        cursor,
                        0,
                        0,
                        new[] { "The source changed while it was read; no records or cursor were committed." });
                }

                if (!CurrentPathMatchesIdentity(fullPath, sourceRoot, identity))
                {
                    transaction.Rollback();
                    return new LedgerIngestResult(
                        LedgerIngestStatus.ContinuityUncertain,
                        created ? null : generationId,
                        cursor,
                        0,
                        0,
                        new[]
                        {
                            "The source path changed to a different physical identity while it was read; no records or cursor were committed."
                        });
                }

                var updated = ExecuteNonQuery(
                    transaction,
                    """
                    UPDATE source_cursors
                    SET committed_offset = $cursor, version = version + 1
                    WHERE generation_id = $generation AND committed_offset = $expected;
                    """,
                    ("$cursor", newCursor),
                    ("$generation", generationId),
                    ("$expected", cursor));

                if (updated != 1)
                {
                    transaction.Rollback();
                    throw new LedgerException(
                        LedgerStorageFailure.StaleCursor,
                        "The committed cursor changed concurrently; the batch was rolled back.");
                }

                CommitWithPostconditions(
                    transaction,
                    jobId,
                    generationId,
                    newCursor,
                    cursorVersion + 1,
                    rangeStart: cursor,
                    rangeEnd: newCursor,
                    expectedRecords: rangeRecordCount,
                    expectedEvents: rangeEventCount);
                return new LedgerIngestResult(
                    LedgerIngestStatus.Committed,
                    generationId,
                    newCursor,
                    recordCount,
                    eventCount,
                    Array.Empty<string>());
            }
            catch (LedgerException)
            {
                TryRollback(transaction);
                throw;
            }
            catch (SqliteException ex)
            {
                TryRollback(transaction);
                throw MapSqliteException(ex, "The ledger transaction failed and was rolled back.");
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException)
            {
                TryRollback(transaction);
                return new LedgerIngestResult(
                    LedgerIngestStatus.SourceUnavailable,
                    null,
                    0,
                    0,
                    0,
                    new[] { "The source became unavailable during the ledger transaction." });
            }
            finally
            {
                transaction?.Dispose();
            }
        }
    }

    public IReadOnlyList<LedgerGeneration> ListGenerations(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT g.generation_id, g.job_id, g.source_path, g.volume_serial,
                           g.file_index, g.creation_utc_ticks, c.committed_offset,
                           c.version, g.first_seen_utc, g.last_seen_utc,
                           g.predecessor_generation_id
                    FROM source_generations g
                    JOIN source_cursors c ON c.generation_id = g.generation_id
                    WHERE g.job_id = $job
                    ORDER BY g.generation_id;
                    """;
                command.Parameters.AddWithValue("$job", jobId);

                var results = new List<LedgerGeneration>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadGeneration(reader));
                }

                return results;
            }
            catch (SqliteException ex)
            {
                throw MapSqliteException(ex, "The ledger generations could not be read.");
            }
        }
    }

    public IReadOnlyList<LedgerRecord> ListRecords(long generationId) =>
        ListRecords(generationId, afterStartByte: null, DefaultReadLimit);

    public IReadOnlyList<LedgerRecord> ListRecords(
        long generationId,
        long? afterStartByte,
        int limit)
    {
        ValidateReadLimit(limit);
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT record_id, generation_id, start_byte, end_byte, payload_length,
                           disposition, content_sha256
                    FROM source_records
                    WHERE generation_id = $generation
                      AND ($after IS NULL OR start_byte > $after)
                    ORDER BY start_byte
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$generation", generationId);
                command.Parameters.AddWithValue(
                    "$after",
                    afterStartByte is null ? DBNull.Value : afterStartByte.Value);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<LedgerRecord>(Math.Min(limit, 256));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new LedgerRecord(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        Enum.Parse<LedgerRecordDisposition>(reader.GetString(5)),
                        reader.GetString(6)));
                }

                return results;
            }
            catch (SqliteException ex)
            {
                throw MapSqliteException(ex, "The ledger records could not be read.");
            }
        }
    }

    public IReadOnlyList<LedgerEventMetadata> ListEvents(long generationId) =>
        ListEvents(generationId, afterEventId: null, DefaultReadLimit);

    public IReadOnlyList<LedgerEventMetadata> ListEvents(
        long generationId,
        long? afterEventId,
        int limit)
    {
        ValidateReadLimit(limit);
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT e.event_id, e.record_id, e.job_id, e.event_key, e.session_id,
                           e.event_timestamp_utc, e.observed_at_utc, e.category,
                           e.severity, e.summary
                    FROM source_events e
                    JOIN source_records r ON r.record_id = e.record_id
                    WHERE r.generation_id = $generation
                      AND ($after IS NULL OR e.event_id > $after)
                    ORDER BY e.event_id
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$generation", generationId);
                command.Parameters.AddWithValue(
                    "$after",
                    afterEventId is null ? DBNull.Value : afterEventId.Value);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<LedgerEventMetadata>(Math.Min(limit, 256));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new LedgerEventMetadata(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5)
                            ? null
                            : LedgerStoragePaths.ParseTimestamp(reader.GetString(5)),
                        LedgerStoragePaths.ParseTimestamp(reader.GetString(6)),
                        Enum.Parse<MonitoringEventCategory>(reader.GetString(7)),
                        Enum.Parse<MonitoringEventSeverity>(reader.GetString(8)),
                        reader.GetString(9)));
                }

                return results;
            }
            catch (SqliteException ex)
            {
                throw MapSqliteException(ex, "The ledger events could not be read.");
            }
        }
    }

    private static void ValidateReadLimit(int limit)
    {
        if (limit < 1 || limit > MaxReadLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"The ledger read limit must be between 1 and {MaxReadLimit.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static LedgerRecordDisposition AnalyzeRecord(
        ReadOnlySpan<byte> content,
        string jobId,
        DateTimeOffset observedAt,
        out ParsedLogEvent? parsed,
        out string contentSha256)
    {
        parsed = null;
        contentSha256 = Convert.ToHexString(SHA256.HashData(content));

        // The caller's maximum line bound is applied before decoding. An
        // overlong record is still recorded by its complete byte range and
        // non-identity hash, but no event metadata is emitted.
        if (content.Length == 0)
        {
            return LedgerRecordDisposition.Empty;
        }

        string line;
        try
        {
            line = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return LedgerRecordDisposition.InvalidUtf8;
        }

        if (!ForgeEventParser.TryParse(line, jobId, observedAt, out parsed) || parsed is null)
        {
            return LedgerRecordDisposition.MalformedJson;
        }

        return LedgerRecordDisposition.Accepted;
    }

    private LedgerRecordDisposition AnalyzeRecord(
        ReadOnlySpan<byte> content,
        string jobId,
        DateTimeOffset observedAt,
        out ParsedLogEvent? parsed,
        out string contentSha256,
        int maxLineBytes)
    {
        if (content.Length > maxLineBytes)
        {
            parsed = null;
            contentSha256 = Convert.ToHexString(SHA256.HashData(content));
            return LedgerRecordDisposition.Overlong;
        }

        return AnalyzeRecord(content, jobId, observedAt, out parsed, out contentSha256);
    }

    private void ConfigureConnectionDefaults()
    {
        ExecuteNonQuery(null, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(null, $"PRAGMA busy_timeout = {_options.EffectiveBusyTimeout.ToString(CultureInfo.InvariantCulture)};");
        ExecuteNonQuery(null, "PRAGMA temp_store = MEMORY;");
        ExecuteNonQuery(null, "PRAGMA trusted_schema = OFF;");
    }

    private void ConfigureConnectionDurability()
    {
        // journal_mode is persistent in the database header. It is applied only
        // after the exact schema has been validated or initialized so that an
        // incompatible existing database is not rewritten by this switch.
        ExecuteScalar(null, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(null, "PRAGMA synchronous = FULL;");
    }

    private void ValidateExistingDatabaseReadOnly()
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }

        long length;
        try
        {
            length = new FileInfo(_databasePath).Length;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new LedgerException(
                LedgerStorageFailure.Unavailable,
                "The existing ledger database could not be inspected safely.",
                ex);
        }

        if (length == 0)
        {
            // SQLite will initialize this file on the read/write open.
            return;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, _options.EffectiveBusyTimeout / 1000)
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        // Read-only plus query_only means validation cannot change the
        // candidate database or recover/write a journal. SQLite may still
        // inspect sidecars; observable path/link/reparse checks happened
        // before this open.
        ExecuteNonQuery(connection, null, "PRAGMA query_only = ON;");
        ExecuteNonQuery(
            connection,
            null,
            $"PRAGMA busy_timeout = {_options.EffectiveBusyTimeout.ToString(CultureInfo.InvariantCulture)};");
        ExecuteNonQuery(connection, null, "PRAGMA trusted_schema = OFF;");

        var version = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        if (version != EventLedgerSchema.CurrentVersion)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {EventLedgerSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateSchemaObjects(connection, null);
        ValidateSchemaMetadata(connection, null);
    }

    private void ValidateWriteTransaction(SqliteTransaction transaction)
    {
        var version = Convert.ToInt32(
            ExecuteScalar(transaction, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        if (version != EventLedgerSchema.CurrentVersion)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {EventLedgerSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateSchemaObjects(transaction);
        ValidateSchemaMetadata(transaction);
    }

    private void ValidateOrInitializeSchema()
    {
        var version = Convert.ToInt32(
            ExecuteScalar(null, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        var objectCount = Convert.ToInt64(
            ExecuteScalar(null, "SELECT COUNT(*) FROM sqlite_master;"),
            CultureInfo.InvariantCulture);

        // Only a completely empty database is initialized. Any existing
        // object, including an oddly named or SQLite-looking object, makes the
        // file incompatible and it is left untouched.
        if (version == 0 && objectCount == 0)
        {
            InitializeSchema();
        }
        else if (version != EventLedgerSchema.CurrentVersion)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {EventLedgerSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateSchemaObjects();
        ValidateSchemaMetadata();
    }

    private void ValidateSchemaObjects(SqliteTransaction? transaction = null) =>
        ValidateSchemaObjects(Connection, transaction);

    private static void ValidateSchemaObjects(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var expected = new Dictionary<string, (string Table, string? Sql)>(StringComparer.Ordinal);
        foreach (var item in RequiredSchemaObjects)
        {
            expected.Add(
                SchemaKey(item.Type, item.Name),
                (item.Table, item.Sql));
        }

        foreach (var item in ExpectedInternalSchemaObjects)
        {
            expected.Add(
                SchemaKey(item.Type, item.Name),
                (item.Table, item.Sql));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // No LIKE filter and no implicit assumption that every name starting
        // with "sqlite" is an internal object. SQLite's LIKE treats '_' as a
        // wildcard, so the previous 'sqlite_%' filter could hide a trigger
        // named e.g. sqliteXdrop_events. Every actual object is enumerated and
        // must match this complete allowlist exactly.
        command.CommandText =
            "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var name = reader.GetString(1);
            var tableName = reader.GetString(2);
            var sql = reader.IsDBNull(3) ? null : reader.GetString(3);
            var key = SchemaKey(type, name);

            if (!expected.TryGetValue(key, out var descriptor) ||
                !string.Equals(tableName, descriptor.Table, StringComparison.Ordinal) ||
                !SqlMatches(sql, descriptor.Sql))
            {
                throw new LedgerException(
                    LedgerStorageFailure.IncompatibleSchema,
                    $"The ledger schema object '{type}:{name}' is unexpected or incompatible; existing data was not modified.");
            }

            if (!seen.Add(key))
            {
                throw new LedgerException(
                    LedgerStorageFailure.IncompatibleSchema,
                    $"The ledger schema object '{type}:{name}' is duplicated; existing data was not modified.");
            }
        }

        if (seen.Count != expected.Count)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                "The ledger schema is incomplete; existing data was not modified.");
        }
    }

    private void ValidateSchemaMetadata(SqliteTransaction? transaction = null) =>
        ValidateSchemaMetadata(Connection, transaction);

    private static void ValidateSchemaMetadata(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var schemaText = ExecuteScalar(
            connection,
            transaction,
            "SELECT value FROM ledger_meta WHERE key = 'schema_version';") as string;
        if (!string.Equals(
                schemaText,
                EventLedgerSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                "The ledger metadata does not match the supported schema; existing data was not modified.");
        }
    }

    private static string SchemaKey(string type, string name) => type + "\0" + name;

    private static bool SqlMatches(string? actual, string? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return string.Equals(NormalizeSql(actual), NormalizeSql(expected), StringComparison.Ordinal);
    }

    private static string NormalizeSql(string sql) =>
        string.Join(
            ' ',
            sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static readonly (string Type, string Name, string Table, string Sql)[] RequiredSchemaObjects =
    {
        (
            "table",
            "ledger_meta",
            "ledger_meta",
            """
            CREATE TABLE ledger_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """
        ),
        (
            "table",
            "jobs",
            "jobs",
            """
            CREATE TABLE jobs (
                job_id TEXT PRIMARY KEY,
                created_utc TEXT NOT NULL
            )
            """
        ),
        (
            "table",
            "source_generations",
            "source_generations",
            """
            CREATE TABLE source_generations (
                generation_id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL REFERENCES jobs(job_id),
                source_path TEXT NOT NULL,
                volume_serial INTEGER NOT NULL,
                file_index TEXT NOT NULL,
                creation_utc_ticks INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                predecessor_generation_id INTEGER NULL
                    REFERENCES source_generations(generation_id)
            )
            """
        ),
        (
            "index",
            "ix_source_generations_identity",
            "source_generations",
            """
            CREATE INDEX ix_source_generations_identity
                ON source_generations(job_id, volume_serial, file_index, creation_utc_ticks)
            """
        ),
        (
            "table",
            "source_cursors",
            "source_cursors",
            """
            CREATE TABLE source_cursors (
                generation_id INTEGER PRIMARY KEY
                    REFERENCES source_generations(generation_id),
                committed_offset INTEGER NOT NULL CHECK (committed_offset >= 0),
                version INTEGER NOT NULL CHECK (version >= 0)
            )
            """
        ),
        (
            "table",
            "source_records",
            "source_records",
            """
            CREATE TABLE source_records (
                record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                generation_id INTEGER NOT NULL
                    REFERENCES source_generations(generation_id),
                start_byte INTEGER NOT NULL CHECK (start_byte >= 0),
                end_byte INTEGER NOT NULL CHECK (end_byte > start_byte),
                payload_length INTEGER NOT NULL CHECK (payload_length >= 0),
                disposition TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                committed_utc TEXT NOT NULL,
                UNIQUE (generation_id, start_byte),
                CHECK (end_byte = start_byte + payload_length + 1)
            )
            """
        ),
        (
            "index",
            "ix_source_records_generation",
            "source_records",
            """
            CREATE INDEX ix_source_records_generation
                ON source_records(generation_id, start_byte)
            """
        ),
        (
            "table",
            "source_events",
            "source_events",
            """
            CREATE TABLE source_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                record_id INTEGER NOT NULL UNIQUE
                    REFERENCES source_records(record_id),
                job_id TEXT NOT NULL,
                event_key TEXT NOT NULL,
                session_id TEXT NULL,
                event_timestamp_utc TEXT NULL,
                observed_at_utc TEXT NOT NULL,
                category TEXT NOT NULL,
                severity TEXT NOT NULL,
                summary TEXT NOT NULL
            )
            """
        ),
        (
            "index",
            "ix_source_events_job",
            "source_events",
            """
            CREATE INDEX ix_source_events_job
                ON source_events(job_id, event_id)
            """
        )
    };

    // Exact SQLite-owned objects expected for this schema. sqlite_sequence
    // exists because three tables use AUTOINCREMENT. The sqlite_autoindex_*
    // rows are SQLite's implementation of the declared PRIMARY KEY and UNIQUE
    // constraints; their SQL text is NULL by design. Any other internal-looking
    // name is rejected as unexpected.
    private static readonly (string Type, string Name, string Table, string? Sql)[]
        ExpectedInternalSchemaObjects =
        {
            (
                "table",
                "sqlite_sequence",
                "sqlite_sequence",
                "CREATE TABLE sqlite_sequence(name,seq)"
            ),
            ("index", "sqlite_autoindex_jobs_1", "jobs", null),
            ("index", "sqlite_autoindex_ledger_meta_1", "ledger_meta", null),
            ("index", "sqlite_autoindex_source_events_1", "source_events", null),
            ("index", "sqlite_autoindex_source_records_1", "source_records", null)
        };

    private void InitializeSchema()
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            foreach (var schemaObject in RequiredSchemaObjects)
            {
                ExecuteNonQuery(transaction, schemaObject.Sql);
            }

            ExecuteNonQuery(
                transaction,
                "INSERT INTO ledger_meta(key, value) VALUES ('schema_version', '1');");
            ExecuteNonQuery(transaction, "PRAGMA user_version = 1;");
            transaction.Commit();
        }
        catch
        {
            TryRollback(transaction);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private void EnsureJob(
        string jobId,
        DateTimeOffset now,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO jobs(job_id, created_utc)
            VALUES ($job, $now)
            ON CONFLICT(job_id) DO NOTHING;
            """,
            ("$job", jobId),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));
    }

    private List<LedgerGeneration> ReadMatchingGenerations(
        SqliteTransaction transaction,
        string jobId,
        LedgerSourceIdentity identity)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT g.generation_id, g.job_id, g.source_path, g.volume_serial,
                   g.file_index, g.creation_utc_ticks, c.committed_offset,
                   c.version, g.first_seen_utc, g.last_seen_utc,
                   g.predecessor_generation_id
            FROM source_generations g
            JOIN source_cursors c ON c.generation_id = g.generation_id
            WHERE g.job_id = $job
              AND g.volume_serial = $volume
              AND g.file_index = $fileIndex
              AND g.creation_utc_ticks = $creation
            ORDER BY g.generation_id;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$volume", (long)identity.VolumeSerial);
        command.Parameters.AddWithValue(
            "$fileIndex",
            identity.FileIndex.ToString("X16", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$creation", identity.CreationTimeUtcTicks);

        var results = new List<LedgerGeneration>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadGeneration(reader));
        }

        return results;
    }

    private long InsertGeneration(
        SqliteTransaction transaction,
        string jobId,
        string sourcePath,
        LedgerSourceIdentity identity,
        DateTimeOffset now)
    {
        var predecessor = ExecuteScalar(
            transaction,
            "SELECT generation_id FROM source_generations WHERE job_id = $job ORDER BY generation_id DESC LIMIT 1;",
            ("$job", jobId));
        long? predecessorId = predecessor is null or DBNull
            ? null
            : Convert.ToInt64(predecessor, CultureInfo.InvariantCulture);

        var generationInserted = ExecuteNonQuery(
            transaction,
            """
            INSERT INTO source_generations(
                job_id, source_path, volume_serial, file_index,
                creation_utc_ticks, first_seen_utc, last_seen_utc,
                predecessor_generation_id)
            VALUES (
                $job, $path, $volume, $fileIndex,
                $creation, $now, $now,
                $predecessor);
            """,
            ("$job", jobId),
            ("$path", sourcePath),
            ("$volume", (long)identity.VolumeSerial),
            ("$fileIndex", identity.FileIndex.ToString("X16", CultureInfo.InvariantCulture)),
            ("$creation", identity.CreationTimeUtcTicks),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)),
            ("$predecessor", predecessorId));
        if (generationInserted != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source generation insert did not take effect; the batch was rolled back.");
        }

        var generationId = Convert.ToInt64(
            ExecuteScalar(transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);

        var cursorInserted = ExecuteNonQuery(
            transaction,
            "INSERT INTO source_cursors(generation_id, committed_offset, version) VALUES ($generation, 0, 0);",
            ("$generation", generationId));
        if (cursorInserted != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source cursor insert did not take effect; the batch was rolled back.");
        }

        return generationId;
    }

    private static void UpdateGenerationObservation(
        SqliteTransaction transaction,
        long generationId,
        string sourcePath,
        DateTimeOffset now)
    {
        // A rename is allowed only because the physical identity was already
        // verified from the opened handle. The path is descriptive, never an
        // identity key.
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE source_generations
            SET source_path = $path, last_seen_utc = $now
            WHERE generation_id = $generation;
            """;
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$now", LedgerStoragePaths.FormatTimestamp(now));
        command.Parameters.AddWithValue("$generation", generationId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source generation update did not take effect; the batch was rolled back.");
        }
    }

    private void CommitWithPostconditions(
        SqliteTransaction transaction,
        string jobId,
        long generationId,
        long expectedCursor,
        long expectedCursorVersion,
        long? rangeStart,
        long? rangeEnd,
        int expectedRecords,
        int expectedEvents)
    {
        ValidateCommittedState(
            Connection,
            transaction,
            jobId,
            generationId,
            expectedCursor,
            expectedCursorVersion,
            rangeStart,
            rangeEnd,
            expectedRecords,
            expectedEvents);
        transaction.Commit();
    }

    internal static void ValidateCommittedState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        long generationId,
        long expectedCursor,
        long expectedCursorVersion,
        long? rangeStart,
        long? rangeEnd,
        int expectedRecords,
        int expectedEvents)
    {
        // Affected-row counts are insufficient: an AFTER INSERT trigger can
        // delete a row while the insert still reports one affected row. Before
        // committing, re-read the required job, generation, cursor, records,
        // and events through the same transaction and require the exact
        // expected state. Any mismatch rolls the whole batch back.
        var jobCount = Convert.ToInt64(
            ExecuteScalar(
                connection,
                transaction,
                "SELECT COUNT(*) FROM jobs WHERE job_id = $job;",
                ("$job", jobId)),
            CultureInfo.InvariantCulture);
        if (jobCount != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The job postcondition failed; the batch was rolled back.");
        }

        var generationCount = Convert.ToInt64(
            ExecuteScalar(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM source_generations
                WHERE generation_id = $generation AND job_id = $job;
                """,
                ("$generation", generationId),
                ("$job", jobId)),
            CultureInfo.InvariantCulture);
        if (generationCount != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source generation postcondition failed; the batch was rolled back.");
        }

        var cursorCount = Convert.ToInt64(
            ExecuteScalar(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM source_cursors
                WHERE generation_id = $generation
                  AND committed_offset = $cursor
                  AND version = $version;
                """,
                ("$generation", generationId),
                ("$cursor", expectedCursor),
                ("$version", expectedCursorVersion)),
            CultureInfo.InvariantCulture);
        if (cursorCount != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The committed cursor postcondition failed; the batch was rolled back.");
        }

        if (rangeStart is { } start && rangeEnd is { } end && end > start)
        {
            var recordCount = Convert.ToInt64(
                ExecuteScalar(
                    connection,
                    transaction,
                    """
                    SELECT COUNT(*)
                    FROM source_records
                    WHERE generation_id = $generation
                      AND start_byte >= $start
                      AND start_byte < $end;
                    """,
                    ("$generation", generationId),
                    ("$start", start),
                    ("$end", end)),
                CultureInfo.InvariantCulture);
            if (recordCount != expectedRecords)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The committed source-record postcondition failed; the batch was rolled back.");
            }

            var eventCount = Convert.ToInt64(
                ExecuteScalar(
                    connection,
                    transaction,
                    """
                    SELECT COUNT(*)
                    FROM source_events e
                    JOIN source_records r ON r.record_id = e.record_id
                    WHERE r.generation_id = $generation
                      AND r.start_byte >= $start
                      AND r.start_byte < $end;
                    """,
                    ("$generation", generationId),
                    ("$start", start),
                    ("$end", end)),
                CultureInfo.InvariantCulture);
            if (eventCount != expectedEvents)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The committed source-event postcondition failed; the batch was rolled back.");
            }
        }
    }

    private (long RecordId, bool Inserted) InsertRecord(
        SqliteTransaction transaction,
        long generationId,
        long startByte,
        long endByte,
        long payloadLength,
        LedgerRecordDisposition disposition,
        string contentSha256,
        DateTimeOffset now)
    {
        var existing = ReadExistingRecord(
            transaction,
            generationId,
            startByte);
        if (existing is { } match)
        {
            // (generation, start) is the uniqueness key. A retry may reuse the
            // range only when its end and content identity validate exactly;
            // the content hash is validation data, never the uniqueness key.
            if (match.EndByte != endByte ||
                match.PayloadLength != payloadLength ||
                match.Disposition != disposition ||
                !string.Equals(match.ContentSha256, contentSha256, StringComparison.Ordinal))
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "A different record already occupies this source-generation byte start; the batch was rolled back.");
            }

            return (match.RecordId, false);
        }

        var inserted = ExecuteNonQuery(
            transaction,
            """
            INSERT INTO source_records(
                generation_id, start_byte, end_byte, payload_length,
                disposition, content_sha256, committed_utc)
            VALUES (
                $generation, $start, $end, $payloadLength,
                $disposition, $hash, $now);
            """,
            ("$generation", generationId),
            ("$start", startByte),
            ("$end", endByte),
            ("$payloadLength", payloadLength),
            ("$disposition", disposition.ToString()),
            ("$hash", contentSha256),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));
        if (inserted != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source record insert did not take effect; the batch was rolled back.");
        }

        return (
            Convert.ToInt64(
                ExecuteScalar(transaction, "SELECT last_insert_rowid();"),
                CultureInfo.InvariantCulture),
            true);
    }

    private (long RecordId, long EndByte, long PayloadLength,
        LedgerRecordDisposition Disposition, string ContentSha256)? ReadExistingRecord(
        SqliteTransaction transaction,
        long generationId,
        long startByte)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT record_id, end_byte, payload_length, disposition, content_sha256
            FROM source_records
            WHERE generation_id = $generation AND start_byte = $start;
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$start", startByte);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            Enum.Parse<LedgerRecordDisposition>(reader.GetString(3)),
            reader.GetString(4));
    }

    private void InsertEvent(
        SqliteTransaction transaction,
        long recordId,
        MonitoringEvent item)
    {
        var inserted = ExecuteNonQuery(
            transaction,
            """
            INSERT INTO source_events(
                record_id, job_id, event_key, session_id, event_timestamp_utc,
                observed_at_utc, category, severity, summary)
            VALUES (
                $record, $job, $eventKey, $session, $eventTimestamp,
                $observedAt, $category, $severity, $summary);
            """,
            ("$record", recordId),
            ("$job", item.JobId),
            ("$eventKey", item.EventKey),
            ("$session", string.IsNullOrWhiteSpace(item.SessionId) ? null : item.SessionId),
            ("$eventTimestamp", item.Timestamp is null
                ? null
                : LedgerStoragePaths.FormatTimestamp(item.Timestamp.Value)),
            ("$observedAt", LedgerStoragePaths.FormatTimestamp(item.ObservedAt)),
            ("$category", item.Category.ToString()),
            ("$severity", item.Severity.ToString()),
            ("$summary", item.Summary));
        if (inserted != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The source event insert did not take effect; the batch was rolled back.");
        }
    }

    private bool CurrentPathMatchesIdentity(
        string fullPath,
        string sourceRoot,
        LedgerSourceIdentity expected)
    {
        using var recheck = SafeFileAccess.OpenRead(fullPath, sourceRoot);
        if (recheck.Stream is null)
        {
            return false;
        }

        var current = _identityReader(recheck.Stream);
        return current.IsVerified && current.Equals(expected);
    }

    private static int ReadFully(FileStream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static LedgerGeneration ReadGeneration(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            new LedgerSourceIdentity(
                checked((uint)reader.GetInt64(3)),
                ulong.Parse(reader.GetString(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                reader.GetInt64(5)),
            reader.GetInt64(6),
            reader.GetInt64(7),
            LedgerStoragePaths.ParseTimestamp(reader.GetString(8)),
            LedgerStoragePaths.ParseTimestamp(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetInt64(10));

    private static string ResolveAndValidateStorageRoot(
        string databasePath,
        string? allowedRoot)
    {
        var roots = allowedRoot is null
            ? new[] { LedgerStoragePaths.DefaultDataDirectory, Path.GetTempPath() }
            : new[] { allowedRoot };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (databasePath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                databasePath.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRoot;
            }
        }

        throw new ArgumentException(
            "The ledger database must be inside the approved local data or isolated temporary storage root.",
            nameof(databasePath));
    }

    private object? ExecuteScalar(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters) =>
        ExecuteScalar(Connection, transaction, sql, parameters);

    private static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    private int ExecuteNonQuery(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters) =>
        ExecuteNonQuery(Connection, transaction, sql, parameters);

    private static int ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
        {
            // The original failure is the actionable error.
        }
    }

    private static LedgerException MapSqliteException(SqliteException exception, string message)
    {
        var failure = exception.SqliteErrorCode switch
        {
            5 or 6 => LedgerStorageFailure.Locked,
            11 or 26 => LedgerStorageFailure.Corrupt,
            19 => LedgerStorageFailure.ConstraintViolation,
            _ => LedgerStorageFailure.Unavailable
        };

        return new LedgerException(failure, message, exception);
    }

    private static LedgerSourceIdentity ReadWindowsIdentity(FileStream stream)
    {
        try
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            {
                return default;
            }

            var fileIndex =
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var creationTicks =
                ((long)information.CreationTime.dwHighDateTime << 32) |
                (uint)information.CreationTime.dwLowDateTime;

            return new LedgerSourceIdentity(
                information.VolumeSerialNumber,
                fileIndex,
                creationTicks);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return default;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EventLedger));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);
}
