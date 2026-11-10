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

    private const string LogicalKeyVersion = "b2v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly EventLedgerOptions _options;
    private readonly EventLedgerSchemaProfile _schema;
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
        : this(
            databasePath,
            allowedRoot,
            options,
            identityReader,
            EventLedgerSchemaProfile.V1)
    {
    }

    /// <summary>
    /// Opens the B2 projection schema in its own fresh database. The B1
    /// constructor above remains the version-1 physical-only contract.
    /// </summary>
    public static EventLedger OpenProjection(
        string databasePath,
        string? allowedRoot = null,
        EventLedgerOptions? options = null,
        LedgerSourceIdentityReader? identityReader = null) =>
        new(
            databasePath,
            allowedRoot,
            options,
            identityReader,
            EventLedgerSchemaProfile.Projection);

    /// <summary>
    /// Opens the V3 foundational schema in its own fresh database. Existing B1,
    /// B2, and V3 databases are version-checked and never migrated. This is a
    /// persistence profile only; the live monitoring pipeline is not cut over.
    /// </summary>
    public static EventLedger OpenV3Projection(
        string databasePath,
        string? allowedRoot = null,
        EventLedgerOptions? options = null,
        LedgerSourceIdentityReader? identityReader = null) =>
        new(
            databasePath,
            allowedRoot,
            options,
            identityReader,
            EventLedgerSchemaProfile.V3);

    private EventLedger(
        string databasePath,
        string? allowedRoot,
        EventLedgerOptions? options,
        LedgerSourceIdentityReader? identityReader,
        EventLedgerSchemaProfile schema)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A ledger database path is required.", nameof(databasePath));
        }

        _schema = schema;
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
                if (_schema.SupportsProjection)
                {
                    var conflictDetail = ExecuteScalar(
                        transaction,
                        "SELECT conflict_detail FROM jobs WHERE job_id = $job;",
                        ("$job", jobId)) as string;
                    if (!string.IsNullOrWhiteSpace(conflictDetail))
                    {
                        transaction.Rollback();
                        return new LedgerIngestResult(
                            LedgerIngestStatus.IdentityConflict,
                            null,
                            0,
                            0,
                            0,
                            new[]
                            {
                                "The durable job has a conflicting runs-root or metadata association; ingestion was refused without mutation."
                            });
                    }

                    TouchJobSource(
                        transaction,
                        jobId,
                        fullPath,
                        "Clean",
                        now);
                }

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
                    generationId = InsertGeneration(
                        transaction,
                        jobId,
                        fullPath,
                        identity,
                        now,
                        length);
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
                    var noDataOutcome = FinalizeProjectionBatch(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        new ProjectionBatchStats(),
                        partialTrailing: false,
                        now);
                    CommitWithPostconditions(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        cursorVersion,
                        rangeStart: null,
                        rangeEnd: null,
                        expectedRecords: 0,
                        expectedEvents: 0,
                        expectedProjectionVersion: noDataOutcome?.Aggregate.Version,
                        expectedLogicalEvents: null);
                    return new LedgerIngestResult(
                        created ? LedgerIngestStatus.Committed : LedgerIngestStatus.NoData,
                        generationId,
                        cursor,
                        0,
                        0,
                        Array.Empty<string>())
                    {
                        Aggregate = noDataOutcome?.Aggregate,
                        Bootstrap = noDataOutcome?.Bootstrap,
                        Coverage = noDataOutcome?.Coverage
                    };
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

                    var partialOutcome = FinalizeProjectionBatch(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        new ProjectionBatchStats { PartialTrailing = 1 },
                        partialTrailing: true,
                        now);
                    CommitWithPostconditions(
                        transaction,
                        jobId,
                        generationId,
                        cursor,
                        cursorVersion,
                        rangeStart: null,
                        rangeEnd: null,
                        expectedRecords: 0,
                        expectedEvents: 0,
                        expectedProjectionVersion: partialOutcome?.Aggregate.Version,
                        expectedLogicalEvents: null);
                    return new LedgerIngestResult(
                        created ? LedgerIngestStatus.Committed : LedgerIngestStatus.NoData,
                        generationId,
                        cursor,
                        0,
                        0,
                        Array.Empty<string>())
                    {
                        Aggregate = partialOutcome?.Aggregate,
                        Bootstrap = partialOutcome?.Bootstrap,
                        Coverage = partialOutcome?.Coverage,
                        PartialTrailingRecord = _schema.SupportsProjection
                    };
                }

                var completeLength = lastNewline + 1;
                var newCursor = cursor;
                var recordCount = 0;
                var eventCount = 0;
                var rangeRecordCount = 0;
                var rangeEventCount = 0;
                var projectionStats = new ProjectionBatchStats();
                var appliedEvents = new List<MonitoringEvent>();
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
                        projectionStats.AddDisposition(disposition);
                        if (disposition == LedgerRecordDisposition.Accepted && parsed is not null)
                        {
                            var sourceEventId = InsertEvent(transaction, recordId, parsed.Event);
                            eventCount++;
                            if (_schema.SupportsProjection)
                            {
                                var applied = ApplyLogicalProjection(
                                    transaction,
                                    jobId,
                                    recordId,
                                    sourceEventId,
                                    parsed,
                                    now,
                                    projectionStats);
                                if (applied is not null)
                                {
                                    appliedEvents.Add(applied);
                                }
                            }
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

                // A trailing partial record exists only when the bounded read
                // reached the captured end of file and bytes remain after the
                // last complete newline. Unread bytes beyond the window are
                // normal bounded-bootstrap progress, not a partial record.
                var readToCapturedEnd = cursor + read >= length;
                var unreadInWindow = read - completeLength;
                var hasPartialTrailing = readToCapturedEnd && unreadInWindow > 0;
                var batchOutcome = FinalizeProjectionBatch(
                    transaction,
                    jobId,
                    generationId,
                    newCursor,
                    projectionStats,
                    partialTrailing: hasPartialTrailing,
                    now);
                CommitWithPostconditions(
                    transaction,
                    jobId,
                    generationId,
                    newCursor,
                    cursorVersion + 1,
                    rangeStart: cursor,
                    rangeEnd: newCursor,
                    expectedRecords: rangeRecordCount,
                    expectedEvents: rangeEventCount,
                    expectedProjectionVersion: batchOutcome?.Aggregate.Version,
                    expectedLogicalEvents: _schema.SupportsProjection ? rangeEventCount : null);
                return new LedgerIngestResult(
                    LedgerIngestStatus.Committed,
                    generationId,
                    newCursor,
                    recordCount,
                    eventCount,
                    Array.Empty<string>())
                {
                    AcceptedEvents = appliedEvents,
                    Aggregate = batchOutcome?.Aggregate,
                    Bootstrap = batchOutcome?.Bootstrap,
                    Coverage = batchOutcome?.Coverage,
                    PartialTrailingRecord = _schema.SupportsProjection && hasPartialTrailing
                };
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
        if (version != _schema.Version)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {_schema.Version.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateSchemaObjects(connection, null, _schema);
        ValidateSchemaMetadata(connection, null, _schema);
    }

    private void ValidateWriteTransaction(SqliteTransaction transaction)
    {
        var version = Convert.ToInt32(
            ExecuteScalar(transaction, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        if (version != _schema.Version)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {_schema.Version.ToString(CultureInfo.InvariantCulture)}.");
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
        else if (version != _schema.Version)
        {
            throw new LedgerException(
                LedgerStorageFailure.IncompatibleSchema,
                $"Unsupported ledger schema version {version.ToString(CultureInfo.InvariantCulture)}; expected {_schema.Version.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateSchemaObjects();
        ValidateSchemaMetadata();
    }

    private void ValidateSchemaObjects(SqliteTransaction? transaction = null) =>
        ValidateSchemaObjects(Connection, transaction, _schema);

    private static void ValidateSchemaObjects(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EventLedgerSchemaProfile schema)
    {
        var expected = new Dictionary<string, (string Table, string? Sql)>(StringComparer.Ordinal);
        foreach (var item in schema.RequiredObjects)
        {
            expected.Add(
                SchemaKey(item.Type, item.Name),
                (item.Table, item.Sql));
        }

        foreach (var item in schema.InternalObjects)
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
        ValidateSchemaMetadata(Connection, transaction, _schema);

    private static void ValidateSchemaMetadata(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EventLedgerSchemaProfile schema)
    {
        var schemaText = ExecuteScalar(
            connection,
            transaction,
            "SELECT value FROM ledger_meta WHERE key = 'schema_version';") as string;
        if (!string.Equals(
                schemaText,
                schema.Version.ToString(CultureInfo.InvariantCulture),
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

    // B2 projection schema. It keeps the complete B1 physical contract but
    // lives in its own fresh database and adds durable job/source association,
    // structured logical decisions, bootstrap checkpoints, coverage and the
    // authoritative aggregate row. It is never used to open or migrate events.db.
    private static readonly (string Type, string Name, string Table, string Sql)[]
        ProjectionRequiredSchemaObjects =
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
                    created_utc TEXT NOT NULL,
                    runs_root TEXT NULL,
                    metadata_path TEXT NULL,
                    last_seen_utc TEXT NULL,
                    conflict_detail TEXT NULL,
                    provisional INTEGER NOT NULL DEFAULT 0,
                    uncovered_detail TEXT NULL
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
            ),
            (
                "table",
                "job_sources",
                "job_sources",
                """
                CREATE TABLE job_sources (
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    source_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    PRIMARY KEY (job_id, source_path)
                )
                """
            ),
            (
                "table",
                "bootstrap_sources",
                "bootstrap_sources",
                """
                CREATE TABLE bootstrap_sources (
                    generation_id INTEGER PRIMARY KEY
                        REFERENCES source_generations(generation_id),
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    initial_boundary INTEGER NOT NULL CHECK (initial_boundary >= 0),
                    committed_offset INTEGER NOT NULL CHECK (committed_offset >= 0),
                    completed_utc TEXT NULL,
                    status TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                )
                """
            ),
            (
                "index",
                "ix_bootstrap_sources_job",
                "bootstrap_sources",
                """
                CREATE INDEX ix_bootstrap_sources_job
                    ON bootstrap_sources(job_id, generation_id)
                """
            ),
            (
                "table",
                "source_coverage",
                "source_coverage",
                """
                CREATE TABLE source_coverage (
                    generation_id INTEGER PRIMARY KEY
                        REFERENCES source_generations(generation_id),
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    status TEXT NOT NULL,
                    clean_until_offset INTEGER NULL,
                    partial_trailing INTEGER NOT NULL DEFAULT 0,
                    malformed_count INTEGER NOT NULL DEFAULT 0,
                    invalid_count INTEGER NOT NULL DEFAULT 0,
                    overlong_count INTEGER NOT NULL DEFAULT 0,
                    missing_identity_count INTEGER NOT NULL DEFAULT 0,
                    missing_cost_count INTEGER NOT NULL DEFAULT 0,
                    duplicate_count INTEGER NOT NULL DEFAULT 0,
                    conflict_count INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL
                )
                """
            ),
            (
                "index",
                "ix_source_coverage_job",
                "source_coverage",
                """
                CREATE INDEX ix_source_coverage_job
                    ON source_coverage(job_id, generation_id)
                """
            ),
            (
                "table",
                "logical_events",
                "logical_events",
                """
                CREATE TABLE logical_events (
                    logical_event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_event_id INTEGER NOT NULL UNIQUE
                        REFERENCES source_events(event_id),
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    decision TEXT NOT NULL,
                    namespace TEXT NULL,
                    session_key TEXT NULL,
                    message_id TEXT NULL,
                    part_id TEXT NULL,
                    event_type TEXT NULL,
                    logical_key TEXT NULL,
                    cost_text TEXT NULL,
                    cost_expected INTEGER NOT NULL DEFAULT 0,
                    event_timestamp_utc TEXT NULL,
                    observed_at_utc TEXT NOT NULL,
                    applied_utc TEXT NULL
                )
                """
            ),
            (
                "index",
                "ix_logical_events_job",
                "logical_events",
                """
                CREATE INDEX ix_logical_events_job
                    ON logical_events(job_id, logical_event_id)
                """
            ),
            (
                "index",
                "ix_logical_events_key",
                "logical_events",
                """
                CREATE INDEX ix_logical_events_key
                    ON logical_events(job_id, logical_key)
                """
            ),
            (
                "table",
                "job_logical_keys",
                "job_logical_keys",
                """
                CREATE TABLE job_logical_keys (
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    logical_key TEXT NOT NULL,
                    logical_event_id INTEGER NOT NULL
                        REFERENCES logical_events(logical_event_id),
                    cost_text TEXT NULL,
                    event_timestamp_utc TEXT NULL,
                    PRIMARY KEY (job_id, logical_key)
                )
                """
            ),
            (
                "table",
                "job_aggregates",
                "job_aggregates",
                """
                CREATE TABLE job_aggregates (
                    job_id TEXT PRIMARY KEY REFERENCES jobs(job_id),
                    cost_total TEXT NOT NULL DEFAULT '0',
                    cost_event_count INTEGER NOT NULL DEFAULT 0,
                    missing_cost_event_count INTEGER NOT NULL DEFAULT 0,
                    latest_session_id TEXT NULL,
                    last_activity_utc TEXT NULL,
                    logical_event_count INTEGER NOT NULL DEFAULT 0,
                    missing_identity_count INTEGER NOT NULL DEFAULT 0,
                    timeline_ambiguous INTEGER NOT NULL DEFAULT 0,
                    version INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL
                )
                """
            ),
            (
                "table",
                "job_coverage",
                "job_coverage",
                """
                CREATE TABLE job_coverage (
                    job_id TEXT PRIMARY KEY REFERENCES jobs(job_id),
                    status TEXT NOT NULL,
                    detail TEXT NULL,
                    updated_utc TEXT NOT NULL
                )
                """
            ),
            (
                "table",
                "discovery_state",
                "discovery_state",
                """
                CREATE TABLE discovery_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    runs_root TEXT NOT NULL,
                    directory_available INTEGER NOT NULL,
                    warnings TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                )
                """
            )
        };

    // V3 keeps the complete B2 physical, projection, coverage, and aggregate
    // contract unchanged and adds the foundational tables for projects, tasks,
    // delegations, OpenCode sessions/messages, evidence, enrichment, usage and
    // cost attribution, monitoring starts, delivery cuts, and observation
    // context. Jobs gain a nullable project and bounded safe metadata only.
    // Every value is stored as bounded text/integers; no raw message, tool,
    // model, credential, or source content is part of this schema.
    private static readonly (string Type, string Name, string Table, string Sql)[]
        V3RequiredSchemaObjects = BuildV3RequiredSchemaObjects();

    private static (string Type, string Name, string Table, string Sql)[]
        BuildV3RequiredSchemaObjects()
    {
        var b2 = ProjectionRequiredSchemaObjects.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal);

        return new (string Type, string Name, string Table, string Sql)[]
        {
            b2["ledger_meta"],
            (
                "table",
                "projects",
                "projects",
                """
                CREATE TABLE projects (
                    project_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    description TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    CHECK (typeof(project_id) = 'text' AND length(project_id) BETWEEN 1 AND 200 AND instr(project_id, char(0)) = 0),
                    CHECK (typeof(display_name) = 'text' AND length(display_name) BETWEEN 1 AND 500 AND instr(display_name, char(0)) = 0),
                    CHECK (description IS NULL OR (typeof(description) = 'text' AND length(description) <= 4096 AND instr(description, char(0)) = 0))
                )
                """
            ),
            (
                "table",
                "project_paths",
                "project_paths",
                """
                CREATE TABLE project_paths (
                    project_path_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL REFERENCES projects(project_id),
                    original_path TEXT NOT NULL,
                    normalized_path TEXT NULL,
                    path_kind TEXT NOT NULL CHECK (path_kind IN ('root', 'worktree', 'other')),
                    case_policy TEXT NOT NULL CHECK (case_policy IN ('sensitive', 'insensitive')),
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                    confirmed_utc TEXT NULL,
                    alias_of_project_path_id INTEGER NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    UNIQUE (project_id, original_path),
                    UNIQUE (project_path_id, project_id),
                    FOREIGN KEY (alias_of_project_path_id, project_id)
                        REFERENCES project_paths(project_path_id, project_id),
                    CHECK (typeof(project_id) = 'text' AND length(project_id) BETWEEN 1 AND 200 AND instr(project_id, char(0)) = 0),
                    CHECK (typeof(original_path) = 'text' AND length(original_path) BETWEEN 1 AND 4096 AND instr(original_path, char(0)) = 0),
                    CHECK (normalized_path IS NULL OR (typeof(normalized_path) = 'text' AND length(normalized_path) BETWEEN 1 AND 4096 AND instr(normalized_path, char(0)) = 0)),
                    CHECK (is_active = 0 OR confirmed_utc IS NOT NULL),
                    CHECK (confirmed_utc IS NOT NULL OR normalized_path IS NULL),
                    CHECK (alias_of_project_path_id IS NULL OR alias_of_project_path_id <> project_path_id),
                    CHECK (confirmed_utc IS NULL OR (
                        case_policy = 'insensitive'
                        AND normalized_path IS NOT NULL
                        AND original_path NOT GLOB '*[^ -~]*'
                        AND substr(replace(original_path, '/', '\'), 1, 1) GLOB '[A-Za-z]'
                        AND substr(replace(original_path, '/', '\'), 2, 2) = ':\'
                        AND normalized_path = lower(replace(original_path, '/', '\'))
                        AND normalized_path NOT GLOB '*[^ -~]*'
                        AND (length(normalized_path) = 3 OR normalized_path NOT GLOB '*\')
                        AND normalized_path NOT GLOB '*\.\*'
                        AND normalized_path NOT GLOB '*\..\*'
                        AND normalized_path NOT GLOB '*\.'
                        AND normalized_path NOT GLOB '*\..'
                        AND instr(normalized_path, '\\') = 0
                    ))
                )
                """
            ),
            (
                "index",
                "ux_project_paths_active_owner",
                "project_paths",
                """
                CREATE UNIQUE INDEX ux_project_paths_active_owner
                    ON project_paths(normalized_path)
                    WHERE confirmed_utc IS NOT NULL AND alias_of_project_path_id IS NULL
                """
            ),
            (
                "index",
                "ix_project_paths_project",
                "project_paths",
                """
                CREATE INDEX ix_project_paths_project ON project_paths(project_id)
                """
            ),
            (
                "index",
                "ix_project_paths_normalized",
                "project_paths",
                """
                CREATE INDEX ix_project_paths_normalized ON project_paths(normalized_path)
                """
            ),
            (
                "index",
                "ix_project_paths_alias",
                "project_paths",
                """
                CREATE INDEX ix_project_paths_alias ON project_paths(alias_of_project_path_id)
                """
            ),
            (
                "table",
                "tasks",
                "tasks",
                """
                CREATE TABLE tasks (
                    task_id TEXT NOT NULL PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(project_id),
                    title TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('open', 'active', 'blocked', 'completed', 'failed', 'cancelled', 'unknown')),
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    CHECK (typeof(task_id) = 'text' AND length(task_id) BETWEEN 1 AND 200 AND instr(task_id, char(0)) = 0),
                    CHECK (title IS NULL OR (typeof(title) = 'text' AND length(title) <= 1000 AND instr(title, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ix_tasks_project",
                "tasks",
                """
                CREATE INDEX ix_tasks_project ON tasks(project_id, updated_utc)
                """
            ),
            (
                "table",
                "jobs",
                "jobs",
                """
                CREATE TABLE jobs (
                    job_id TEXT NOT NULL PRIMARY KEY,
                    created_utc TEXT NOT NULL,
                    runs_root TEXT NULL,
                    metadata_path TEXT NULL,
                    last_seen_utc TEXT NULL,
                    conflict_detail TEXT NULL,
                    provisional INTEGER NOT NULL DEFAULT 0,
                    uncovered_detail TEXT NULL,
                    project_id TEXT NULL REFERENCES projects(project_id),
                    display_name TEXT NULL,
                    metadata_summary TEXT NULL,
                    reported_directory TEXT NULL,
                    model TEXT NULL,
                    started_utc TEXT NULL,
                    deadline_utc TEXT NULL,
                    status TEXT NULL,
                    provenance_id INTEGER NULL REFERENCES provenance(provenance_id),
                    CHECK (typeof(job_id) = 'text' AND length(job_id) BETWEEN 1 AND 200 AND instr(job_id, char(0)) = 0),
                    CHECK (provisional IN (0, 1)),
                    CHECK (display_name IS NULL OR (typeof(display_name) = 'text' AND length(display_name) BETWEEN 1 AND 500 AND instr(display_name, char(0)) = 0)),
                    CHECK (metadata_summary IS NULL OR (typeof(metadata_summary) = 'text' AND length(metadata_summary) <= 4096 AND instr(metadata_summary, char(0)) = 0)),
                    CHECK (reported_directory IS NULL OR (typeof(reported_directory) = 'text' AND length(reported_directory) <= 4096 AND instr(reported_directory, char(0)) = 0)),
                    CHECK (model IS NULL OR length(model) <= 500),
                    CHECK (status IS NULL OR length(status) <= 100),
                    CHECK (deadline_utc IS NULL OR started_utc IS NULL OR deadline_utc >= started_utc)
                )
                """
            ),
            (
                "index",
                "ix_jobs_project",
                "jobs",
                """
                CREATE INDEX ix_jobs_project ON jobs(project_id)
                """
            ),
            (
                "index",
                "ix_jobs_provenance",
                "jobs",
                """
                CREATE INDEX ix_jobs_provenance ON jobs(provenance_id)
                """
            ),
            b2["source_generations"],
            b2["ix_source_generations_identity"],
            (
                "index",
                "ux_source_generations_id_job",
                "source_generations",
                """
                CREATE UNIQUE INDEX ux_source_generations_id_job
                    ON source_generations(generation_id, job_id)
                """
            ),
            b2["source_cursors"],
            (
                "index",
                "ux_source_cursors_generation_offset",
                "source_cursors",
                """
                CREATE UNIQUE INDEX ux_source_cursors_generation_offset
                    ON source_cursors(generation_id, committed_offset)
                """
            ),
            b2["source_records"],
            b2["ix_source_records_generation"],
            b2["source_events"],
            b2["ix_source_events_job"],
            b2["job_sources"],
            b2["bootstrap_sources"],
            b2["ix_bootstrap_sources_job"],
            b2["source_coverage"],
            b2["ix_source_coverage_job"],
            b2["logical_events"],
            b2["ix_logical_events_job"],
            b2["ix_logical_events_key"],
            (
                "index",
                "ux_logical_events_id_decision_cost",
                "logical_events",
                """
                CREATE UNIQUE INDEX ux_logical_events_id_decision_cost
                    ON logical_events(logical_event_id, decision, cost_text)
                """
            ),
            b2["job_logical_keys"],
            b2["job_aggregates"],
            b2["job_coverage"],
            b2["discovery_state"],
            (
                "table",
                "task_runs",
                "task_runs",
                """
                CREATE TABLE task_runs (
                    run_id TEXT NOT NULL PRIMARY KEY,
                    task_id TEXT NOT NULL REFERENCES tasks(task_id),
                    job_id TEXT NULL REFERENCES jobs(job_id),
                    association_role TEXT NOT NULL DEFAULT 'primary' CHECK (association_role IN ('primary', 'secondary')),
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                    retired_utc TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'completed', 'failed', 'cancelled', 'unknown')),
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    UNIQUE (run_id, association_role),
                    CHECK (typeof(run_id) = 'text' AND length(run_id) BETWEEN 1 AND 200 AND instr(run_id, char(0)) = 0),
                    CHECK (ended_utc IS NULL OR ended_utc >= started_utc),
                    CHECK (retired_utc IS NULL OR retired_utc >= started_utc),
                    CHECK (is_active = 0 OR retired_utc IS NULL)
                )
                """
            ),
            (
                "index",
                "ux_task_runs_active_primary",
                "task_runs",
                """
                CREATE UNIQUE INDEX ux_task_runs_active_primary
                    ON task_runs(job_id)
                    WHERE job_id IS NOT NULL AND is_active = 1 AND association_role = 'primary'
                """
            ),
            (
                "index",
                "ix_task_runs_task",
                "task_runs",
                """
                CREATE INDEX ix_task_runs_task ON task_runs(task_id, started_utc)
                """
            ),
            (
                "index",
                "ix_task_runs_job",
                "task_runs",
                """
                CREATE INDEX ix_task_runs_job ON task_runs(job_id, association_role, is_active)
                """
            ),
            (
                "trigger",
                "trg_task_runs_primary_project_insert",
                "task_runs",
                """
                CREATE TRIGGER trg_task_runs_primary_project_insert
                BEFORE INSERT ON task_runs
                WHEN NEW.association_role = 'primary' AND NEW.is_active = 1 AND NEW.job_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM tasks t
                        JOIN jobs j ON j.job_id = NEW.job_id
                        WHERE t.task_id = NEW.task_id
                          AND j.project_id IS NOT NULL
                          AND t.project_id <> j.project_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_task_runs_primary_project_update",
                "task_runs",
                """
                CREATE TRIGGER trg_task_runs_primary_project_update
                BEFORE UPDATE ON task_runs
                WHEN NEW.association_role = 'primary' AND NEW.is_active = 1 AND NEW.job_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM tasks t
                        JOIN jobs j ON j.job_id = NEW.job_id
                        WHERE t.task_id = NEW.task_id
                          AND j.project_id IS NOT NULL
                          AND t.project_id <> j.project_id
                    );
                END
                """
            ),
            // REPLACE retains referenced keys without firing UPDATE guards.
            // Check actual inserted rows (after conflict handling), and guard
            // identity-changing UPDATE OR REPLACE against the destination owner.
            (
                "trigger",
                "trg_jobs_project_primary_consistency",
                "jobs",
                """
                CREATE TRIGGER trg_jobs_project_primary_consistency
                BEFORE UPDATE OF job_id, project_id ON jobs
                WHEN NEW.project_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM task_runs r
                        JOIN tasks t ON t.task_id = r.task_id
                        WHERE r.job_id = NEW.job_id
                          AND r.association_role = 'primary'
                          AND r.is_active = 1
                          AND t.project_id <> NEW.project_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_jobs_project_primary_consistency_insert",
                "jobs",
                """
                CREATE TRIGGER trg_jobs_project_primary_consistency_insert
                AFTER INSERT ON jobs
                WHEN NEW.project_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM task_runs r
                        JOIN tasks t ON t.task_id = r.task_id
                        WHERE r.job_id = NEW.job_id
                          AND r.association_role = 'primary'
                          AND r.is_active = 1
                          AND t.project_id <> NEW.project_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_tasks_project_primary_consistency",
                "tasks",
                """
                CREATE TRIGGER trg_tasks_project_primary_consistency
                BEFORE UPDATE OF task_id, project_id ON tasks
                WHEN NEW.project_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM task_runs r
                        JOIN jobs j ON j.job_id = r.job_id
                        WHERE r.task_id = NEW.task_id
                          AND r.association_role = 'primary'
                          AND r.is_active = 1
                          AND j.project_id IS NOT NULL
                          AND j.project_id <> NEW.project_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_tasks_project_primary_consistency_insert",
                "tasks",
                """
                CREATE TRIGGER trg_tasks_project_primary_consistency_insert
                AFTER INSERT ON tasks
                WHEN NEW.project_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'primary task/job project conflict')
                    WHERE EXISTS (
                        SELECT 1
                        FROM task_runs r
                        JOIN jobs j ON j.job_id = r.job_id
                        WHERE r.task_id = NEW.task_id
                          AND r.association_role = 'primary'
                          AND r.is_active = 1
                          AND j.project_id IS NOT NULL
                          AND j.project_id <> NEW.project_id
                    );
                END
                """
            ),
            (
                "table",
                "delegation_invocations",
                "delegation_invocations",
                """
                CREATE TABLE delegation_invocations (
                    invocation_id TEXT NOT NULL PRIMARY KEY,
                    run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                    invocation_index INTEGER NOT NULL CHECK (invocation_index >= 0),
                    provider TEXT NULL,
                    model TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'completed', 'failed', 'cancelled', 'unknown')),
                    started_utc TEXT NOT NULL,
                    completed_utc TEXT NULL,
                    UNIQUE (run_id, invocation_index),
                    CHECK (typeof(invocation_id) = 'text' AND length(invocation_id) BETWEEN 1 AND 200 AND instr(invocation_id, char(0)) = 0),
                    CHECK (completed_utc IS NULL OR completed_utc >= started_utc)
                )
                """
            ),
            (
                "index",
                "ix_delegation_invocations_run",
                "delegation_invocations",
                """
                CREATE INDEX ix_delegation_invocations_run
                    ON delegation_invocations(run_id, invocation_index)
                """
            ),
            (
                "index",
                "ux_delegation_invocations_id_run",
                "delegation_invocations",
                """
                CREATE UNIQUE INDEX ux_delegation_invocations_id_run
                    ON delegation_invocations(invocation_id, run_id)
                """
            ),
            (
                "table",
                "invocation_jobs",
                "invocation_jobs",
                """
                CREATE TABLE invocation_jobs (
                    invocation_id TEXT NOT NULL REFERENCES delegation_invocations(invocation_id),
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    role TEXT NOT NULL DEFAULT 'primary' CHECK (role IN ('primary', 'secondary', 'retry')),
                    PRIMARY KEY (invocation_id, job_id)
                )
                """
            ),
            (
                "index",
                "ix_invocation_jobs_job",
                "invocation_jobs",
                """
                CREATE INDEX ix_invocation_jobs_job ON invocation_jobs(job_id)
                """
            ),
            (
                "table",
                "external_references",
                "external_references",
                """
                CREATE TABLE external_references (
                    reference_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NULL REFERENCES task_runs(run_id),
                    invocation_id TEXT NULL,
                    invocation_run_id TEXT NULL,
                    reference_kind TEXT NOT NULL CHECK (reference_kind IN ('issue', 'pull_request', 'commit', 'document', 'other')),
                    source_system TEXT NOT NULL,
                    external_id TEXT NOT NULL,
                    url TEXT NULL,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (invocation_id, invocation_run_id)
                        REFERENCES delegation_invocations(invocation_id, run_id),
                    CHECK (run_id IS NOT NULL OR invocation_id IS NOT NULL),
                    CHECK ((invocation_id IS NULL AND invocation_run_id IS NULL)
                        OR (invocation_id IS NOT NULL AND invocation_run_id IS NOT NULL)),
                    CHECK (invocation_id IS NULL OR run_id IS NULL OR run_id = invocation_run_id),
                    CHECK (typeof(source_system) = 'text' AND length(source_system) BETWEEN 1 AND 100 AND instr(source_system, char(0)) = 0),
                    CHECK (typeof(external_id) = 'text' AND length(external_id) BETWEEN 1 AND 2048 AND instr(external_id, char(0)) = 0),
                    CHECK (url IS NULL OR (typeof(url) = 'text' AND length(url) <= 4096 AND instr(url, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ux_external_references_namespace",
                "external_references",
                """
                CREATE UNIQUE INDEX ux_external_references_namespace
                    ON external_references(
                        source_system,
                        reference_kind,
                        external_id,
                        COALESCE(run_id, ''),
                        COALESCE(invocation_id, ''))
                """
            ),
            (
                "index",
                "ix_external_references_run",
                "external_references",
                """
                CREATE INDEX ix_external_references_run ON external_references(run_id)
                """
            ),
            (
                "index",
                "ix_external_references_invocation",
                "external_references",
                """
                CREATE INDEX ix_external_references_invocation ON external_references(invocation_id)
                """
            ),
            (
                "table",
                "opencode_sessions",
                "opencode_sessions",
                """
                CREATE TABLE opencode_sessions (
                    source_instance_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    project_id TEXT NULL REFERENCES projects(project_id),
                    parent_session_id TEXT NULL,
                    title TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('active', 'idle', 'completed', 'failed', 'unknown')),
                    started_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    PRIMARY KEY (source_instance_id, session_id),
                    FOREIGN KEY (source_instance_id, parent_session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    CHECK (length(source_instance_id) BETWEEN 1 AND 200),
                    CHECK (length(session_id) BETWEEN 1 AND 200),
                    CHECK (title IS NULL OR length(title) <= 1000),
                    CHECK (ended_utc IS NULL OR ended_utc >= started_utc),
                    CHECK (parent_session_id IS NULL OR parent_session_id <> session_id)
                )
                """
            ),
            (
                "index",
                "ix_opencode_sessions_project",
                "opencode_sessions",
                """
                CREATE INDEX ix_opencode_sessions_project
                    ON opencode_sessions(project_id, updated_utc)
                """
            ),
            (
                "index",
                "ix_opencode_sessions_parent",
                "opencode_sessions",
                """
                CREATE INDEX ix_opencode_sessions_parent
                    ON opencode_sessions(source_instance_id, parent_session_id)
                """
            ),
            (
                "table",
                "messages",
                "messages",
                """
                CREATE TABLE messages (
                    source_instance_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    role TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'idle', 'agent-switched')),
                    sequence INTEGER NOT NULL CHECK (sequence >= 0),
                    parent_message_id TEXT NULL,
                    created_utc TEXT NOT NULL,
                    summary TEXT NULL,
                    PRIMARY KEY (source_instance_id, session_id, message_id),
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    FOREIGN KEY (source_instance_id, session_id, parent_message_id)
                        REFERENCES messages(source_instance_id, session_id, message_id),
                    UNIQUE (source_instance_id, session_id, sequence),
                    CHECK (parent_message_id IS NULL OR parent_message_id <> message_id),
                    CHECK (length(message_id) BETWEEN 1 AND 200),
                    CHECK (summary IS NULL OR length(summary) <= 4096)
                )
                """
            ),
            (
                "index",
                "ix_messages_parent",
                "messages",
                """
                CREATE INDEX ix_messages_parent
                    ON messages(source_instance_id, session_id, parent_message_id)
                """
            ),
            (
                "index",
                "ix_messages_session",
                "messages",
                """
                CREATE INDEX ix_messages_session
                    ON messages(source_instance_id, session_id, sequence)
                """
            ),
            (
                "trigger",
                "trg_messages_parent_no_cycle_insert",
                "messages",
                """
                CREATE TRIGGER trg_messages_parent_no_cycle_insert
                BEFORE INSERT ON messages
                WHEN NEW.parent_message_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'message parent cycle')
                    WHERE EXISTS (
                        WITH RECURSIVE ancestors(source_instance_id, session_id, message_id, parent_message_id) AS (
                            SELECT source_instance_id, session_id, message_id, parent_message_id
                            FROM messages
                            WHERE source_instance_id = NEW.source_instance_id
                              AND session_id = NEW.session_id
                              AND message_id = NEW.parent_message_id
                            UNION
                            SELECT m.source_instance_id, m.session_id, m.message_id, m.parent_message_id
                            FROM messages m
                            JOIN ancestors a
                              ON m.source_instance_id = a.source_instance_id
                             AND m.session_id = a.session_id
                             AND m.message_id = a.parent_message_id
                        )
                        SELECT 1 FROM ancestors WHERE message_id = NEW.message_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_messages_parent_no_cycle_update",
                "messages",
                """
                CREATE TRIGGER trg_messages_parent_no_cycle_update
                BEFORE UPDATE OF parent_message_id ON messages
                WHEN NEW.parent_message_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'message parent cycle')
                    WHERE EXISTS (
                        WITH RECURSIVE ancestors(source_instance_id, session_id, message_id, parent_message_id) AS (
                            SELECT source_instance_id, session_id, message_id, parent_message_id
                            FROM messages
                            WHERE source_instance_id = NEW.source_instance_id
                              AND session_id = NEW.session_id
                              AND message_id = NEW.parent_message_id
                            UNION
                            SELECT m.source_instance_id, m.session_id, m.message_id, m.parent_message_id
                            FROM messages m
                            JOIN ancestors a
                              ON m.source_instance_id = a.source_instance_id
                             AND m.session_id = a.session_id
                             AND m.message_id = a.parent_message_id
                        )
                        SELECT 1 FROM ancestors WHERE message_id = NEW.message_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_sessions_parent_no_cycle_insert",
                "opencode_sessions",
                """
                CREATE TRIGGER trg_sessions_parent_no_cycle_insert
                BEFORE INSERT ON opencode_sessions
                WHEN NEW.parent_session_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'session parent cycle')
                    WHERE EXISTS (
                        WITH RECURSIVE ancestors(source_instance_id, session_id, parent_session_id) AS (
                            SELECT source_instance_id, session_id, parent_session_id
                            FROM opencode_sessions
                            WHERE source_instance_id = NEW.source_instance_id
                              AND session_id = NEW.parent_session_id
                            UNION
                            SELECT s.source_instance_id, s.session_id, s.parent_session_id
                            FROM opencode_sessions s
                            JOIN ancestors a
                              ON s.source_instance_id = a.source_instance_id
                             AND s.session_id = a.parent_session_id
                        )
                        SELECT 1 FROM ancestors WHERE session_id = NEW.session_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_sessions_parent_no_cycle_update",
                "opencode_sessions",
                """
                CREATE TRIGGER trg_sessions_parent_no_cycle_update
                BEFORE UPDATE OF parent_session_id ON opencode_sessions
                WHEN NEW.parent_session_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'session parent cycle')
                    WHERE EXISTS (
                        WITH RECURSIVE ancestors(source_instance_id, session_id, parent_session_id) AS (
                            SELECT source_instance_id, session_id, parent_session_id
                            FROM opencode_sessions
                            WHERE source_instance_id = NEW.source_instance_id
                              AND session_id = NEW.parent_session_id
                            UNION
                            SELECT s.source_instance_id, s.session_id, s.parent_session_id
                            FROM opencode_sessions s
                            JOIN ancestors a
                              ON s.source_instance_id = a.source_instance_id
                             AND s.session_id = a.parent_session_id
                        )
                        SELECT 1 FROM ancestors WHERE session_id = NEW.session_id
                    );
                END
                """
            ),
            (
                "table",
                "execution_segments",
                "execution_segments",
                """
                CREATE TABLE execution_segments (
                    source_instance_id TEXT NOT NULL,
                    segment_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    run_id TEXT NULL REFERENCES task_runs(run_id),
                    status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed', 'cancelled', 'unknown')),
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    PRIMARY KEY (source_instance_id, segment_id),
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    UNIQUE (source_instance_id, segment_id, session_id),
                    UNIQUE (source_instance_id, segment_id, run_id),
                    CHECK (length(segment_id) BETWEEN 1 AND 200),
                    CHECK (ended_utc IS NULL OR ended_utc >= started_utc)
                )
                """
            ),
            (
                "index",
                "ix_execution_segments_session",
                "execution_segments",
                """
                CREATE INDEX ix_execution_segments_session
                    ON execution_segments(source_instance_id, session_id, started_utc)
                """
            ),
            (
                "index",
                "ix_execution_segments_run",
                "execution_segments",
                """
                CREATE INDEX ix_execution_segments_run ON execution_segments(run_id)
                """
            ),
            (
                "table",
                "segment_messages",
                "segment_messages",
                """
                CREATE TABLE segment_messages (
                    source_instance_id TEXT NOT NULL,
                    segment_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    position INTEGER NOT NULL CHECK (position >= 0),
                    PRIMARY KEY (source_instance_id, segment_id, position),
                    UNIQUE (source_instance_id, segment_id, message_id),
                    FOREIGN KEY (source_instance_id, segment_id, session_id)
                        REFERENCES execution_segments(source_instance_id, segment_id, session_id),
                    FOREIGN KEY (source_instance_id, session_id, message_id)
                        REFERENCES messages(source_instance_id, session_id, message_id)
                )
                """
            ),
            (
                "index",
                "ix_segment_messages_message",
                "segment_messages",
                """
                CREATE INDEX ix_segment_messages_message
                    ON segment_messages(source_instance_id, session_id, message_id)
                """
            ),
            (
                "index",
                "ix_segment_messages_session",
                "segment_messages",
                """
                CREATE INDEX ix_segment_messages_session
                    ON segment_messages(source_instance_id, session_id)
                """
            ),
            (
                "table",
                "run_segments",
                "run_segments",
                """
                CREATE TABLE run_segments (
                    run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                    source_instance_id TEXT NOT NULL,
                    segment_id TEXT NOT NULL,
                    position INTEGER NOT NULL CHECK (position >= 0),
                    PRIMARY KEY (run_id, source_instance_id, segment_id),
                    UNIQUE (run_id, position),
                    FOREIGN KEY (source_instance_id, segment_id, run_id)
                        REFERENCES execution_segments(source_instance_id, segment_id, run_id)
                )
                """
            ),
            (
                "index",
                "ix_run_segments_segment",
                "run_segments",
                """
                CREATE INDEX ix_run_segments_segment
                    ON run_segments(source_instance_id, segment_id)
                """
            ),
            (
                "table",
                "content_items",
                "content_items",
                """
                CREATE TABLE content_items (
                    content_id TEXT NOT NULL PRIMARY KEY,
                    source_instance_id TEXT NOT NULL,
                    message_id TEXT NULL,
                    session_id TEXT NULL,
                    segment_id TEXT NULL,
                    content_kind TEXT NOT NULL CHECK (content_kind IN ('text', 'reasoning', 'tool_call', 'tool_result', 'file', 'image', 'other')),
                    summary TEXT NULL,
                    content_sha256 TEXT NULL,
                    byte_length INTEGER NULL CHECK (byte_length >= 0),
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (source_instance_id, session_id, message_id)
                        REFERENCES messages(source_instance_id, session_id, message_id),
                    FOREIGN KEY (source_instance_id, segment_id, session_id)
                        REFERENCES execution_segments(source_instance_id, segment_id, session_id),
                    FOREIGN KEY (source_instance_id, segment_id, message_id)
                        REFERENCES segment_messages(source_instance_id, segment_id, message_id),
                    CHECK (message_id IS NULL OR session_id IS NOT NULL),
                    CHECK (segment_id IS NULL OR session_id IS NOT NULL),
                    CHECK (message_id IS NOT NULL OR segment_id IS NOT NULL),
                    CHECK (typeof(content_id) = 'text' AND length(content_id) BETWEEN 1 AND 200 AND instr(content_id, char(0)) = 0),
                    CHECK (typeof(source_instance_id) = 'text' AND length(source_instance_id) BETWEEN 1 AND 200 AND instr(source_instance_id, char(0)) = 0),
                    CHECK (summary IS NULL OR (typeof(summary) = 'text' AND length(summary) <= 8192 AND instr(summary, char(0)) = 0)),
                    CHECK (content_sha256 IS NULL OR (typeof(content_sha256) = 'text' AND length(content_sha256) = 64 AND content_sha256 GLOB '[0-9a-fA-F]*' AND instr(content_sha256, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ix_content_items_message",
                "content_items",
                """
                CREATE INDEX ix_content_items_message
                    ON content_items(source_instance_id, session_id, message_id)
                """
            ),
            (
                "index",
                "ix_content_items_segment",
                "content_items",
                """
                CREATE INDEX ix_content_items_segment
                    ON content_items(source_instance_id, segment_id)
                """
            ),
            (
                "index",
                "ix_content_items_kind",
                "content_items",
                """
                CREATE INDEX ix_content_items_kind ON content_items(content_kind)
                """
            ),
            (
                "table",
                "results",
                "results",
                """
                CREATE TABLE results (
                    result_id TEXT NOT NULL PRIMARY KEY,
                    run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                    invocation_id TEXT NULL,
                    invocation_run_id TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('passed', 'failed', 'partial', 'skipped', 'unknown')),
                    summary TEXT NULL,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (invocation_id, invocation_run_id)
                        REFERENCES delegation_invocations(invocation_id, run_id),
                    CHECK ((invocation_id IS NULL AND invocation_run_id IS NULL)
                        OR (invocation_id IS NOT NULL AND invocation_run_id IS NOT NULL)),
                    CHECK (invocation_id IS NULL OR invocation_run_id = run_id),
                    CHECK (typeof(result_id) = 'text' AND length(result_id) BETWEEN 1 AND 200 AND instr(result_id, char(0)) = 0),
                    CHECK (summary IS NULL OR (typeof(summary) = 'text' AND length(summary) <= 8192 AND instr(summary, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ux_results_id_run",
                "results",
                """
                CREATE UNIQUE INDEX ux_results_id_run ON results(result_id, run_id)
                """
            ),
            (
                "index",
                "ix_results_run",
                "results",
                """
                CREATE INDEX ix_results_run ON results(run_id)
                """
            ),
            (
                "index",
                "ix_results_invocation",
                "results",
                """
                CREATE INDEX ix_results_invocation ON results(invocation_id)
                """
            ),
            (
                "table",
                "execution_evidence",
                "execution_evidence",
                """
                CREATE TABLE execution_evidence (
                    evidence_id TEXT NOT NULL PRIMARY KEY,
                    result_id TEXT NOT NULL REFERENCES results(result_id),
                    evidence_kind TEXT NOT NULL CHECK (evidence_kind IN ('log', 'test', 'file', 'command', 'url', 'other')),
                    summary TEXT NULL,
                    uri TEXT NULL,
                    observed_utc TEXT NOT NULL,
                    CHECK (typeof(evidence_id) = 'text' AND length(evidence_id) BETWEEN 1 AND 200 AND instr(evidence_id, char(0)) = 0),
                    CHECK (summary IS NULL OR (typeof(summary) = 'text' AND length(summary) <= 8192 AND instr(summary, char(0)) = 0)),
                    CHECK (uri IS NULL OR length(uri) <= 4096)
                )
                """
            ),
            (
                "index",
                "ix_execution_evidence_result",
                "execution_evidence",
                """
                CREATE INDEX ix_execution_evidence_result ON execution_evidence(result_id)
                """
            ),
            (
                "index",
                "ix_execution_evidence_kind",
                "execution_evidence",
                """
                CREATE INDEX ix_execution_evidence_kind ON execution_evidence(evidence_kind)
                """
            ),
            (
                "index",
                "ux_execution_evidence_id_result",
                "execution_evidence",
                """
                CREATE UNIQUE INDEX ux_execution_evidence_id_result
                    ON execution_evidence(evidence_id, result_id)
                """
            ),
            (
                "table",
                "validations",
                "validations",
                """
                CREATE TABLE validations (
                    validation_id TEXT NOT NULL PRIMARY KEY,
                    result_id TEXT NULL,
                    result_run_id TEXT NULL,
                    run_id TEXT NULL REFERENCES task_runs(run_id),
                    validation_kind TEXT NOT NULL CHECK (validation_kind IN ('test', 'build', 'lint', 'review', 'other')),
                    validation_authority TEXT NOT NULL DEFAULT 'unknown'
                        CHECK (validation_authority IN ('worker', 'independent', 'user_acceptance', 'unknown')),
                    status TEXT NOT NULL CHECK (status IN ('passed', 'failed', 'skipped', 'error', 'unknown')),
                    evidence_id TEXT NULL,
                    evidence_result_id TEXT NULL,
                    reviewer_provenance_id INTEGER NULL REFERENCES provenance(provenance_id),
                    is_verified INTEGER NOT NULL DEFAULT 0 CHECK (is_verified IN (0, 1)),
                    detail TEXT NULL,
                    executed_utc TEXT NULL,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (result_id, result_run_id)
                        REFERENCES results(result_id, run_id),
                    FOREIGN KEY (evidence_id, evidence_result_id)
                        REFERENCES execution_evidence(evidence_id, result_id),
                    CHECK (result_id IS NOT NULL OR run_id IS NOT NULL),
                    CHECK ((result_id IS NULL AND result_run_id IS NULL)
                        OR (result_id IS NOT NULL AND result_run_id IS NOT NULL)),
                    CHECK (result_id IS NULL OR run_id IS NULL OR run_id = result_run_id),
                    CHECK ((evidence_id IS NULL AND evidence_result_id IS NULL)
                        OR (evidence_id IS NOT NULL AND evidence_result_id IS NOT NULL)),
                    CHECK (evidence_id IS NULL OR result_id IS NOT NULL),
                    CHECK (evidence_result_id IS NULL OR result_id IS NULL OR evidence_result_id = result_id),
                    CHECK (is_verified = 0 OR evidence_id IS NOT NULL),
                    CHECK (is_verified = 0 OR validation_authority IN ('independent', 'user_acceptance')),
                    CHECK (typeof(validation_id) = 'text' AND length(validation_id) BETWEEN 1 AND 200 AND instr(validation_id, char(0)) = 0),
                    CHECK (detail IS NULL OR (typeof(detail) = 'text' AND length(detail) <= 8192 AND instr(detail, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ix_validations_result",
                "validations",
                """
                CREATE INDEX ix_validations_result ON validations(result_id)
                """
            ),
            (
                "index",
                "ix_validations_run",
                "validations",
                """
                CREATE INDEX ix_validations_run ON validations(run_id, created_utc)
                """
            ),
            (
                "index",
                "ix_validations_status",
                "validations",
                """
                CREATE INDEX ix_validations_status ON validations(status)
                """
            ),
            (
                "trigger",
                "trg_validations_reviewer_provenance_insert",
                "validations",
                """
                CREATE TRIGGER trg_validations_reviewer_provenance_insert
                BEFORE INSERT ON validations
                WHEN NEW.reviewer_provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.reviewer_provenance_id
                          AND (
                            (p.entity_type = 'result' AND p.result_id IS NOT NULL AND p.result_id IS NEW.result_id)
                            OR (p.entity_type = 'run' AND p.run_id IS NOT NULL AND p.run_id IS COALESCE(NEW.run_id, NEW.result_run_id))
                            OR (p.entity_type = 'job' AND p.job_id IS NOT NULL AND p.job_id IS (
                                SELECT r.job_id FROM task_runs r
                                WHERE r.run_id IS COALESCE(NEW.run_id, NEW.result_run_id)))
                            OR (p.entity_type = 'invocation' AND p.invocation_id IS NOT NULL AND EXISTS (
                                SELECT 1 FROM delegation_invocations di
                                WHERE di.invocation_id IS p.invocation_id
                                  AND di.run_id IS COALESCE(NEW.run_id, NEW.result_run_id)))
                          )
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_validations_reviewer_provenance_update",
                "validations",
                """
                CREATE TRIGGER trg_validations_reviewer_provenance_update
                BEFORE UPDATE OF reviewer_provenance_id, result_id, result_run_id, run_id, is_verified ON validations
                WHEN NEW.reviewer_provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.reviewer_provenance_id
                          AND (
                            (p.entity_type = 'result' AND p.result_id IS NOT NULL AND p.result_id IS NEW.result_id)
                            OR (p.entity_type = 'run' AND p.run_id IS NOT NULL AND p.run_id IS COALESCE(NEW.run_id, NEW.result_run_id))
                            OR (p.entity_type = 'job' AND p.job_id IS NOT NULL AND p.job_id IS (
                                SELECT r.job_id FROM task_runs r
                                WHERE r.run_id IS COALESCE(NEW.run_id, NEW.result_run_id)))
                            OR (p.entity_type = 'invocation' AND p.invocation_id IS NOT NULL AND EXISTS (
                                SELECT 1 FROM delegation_invocations di
                                WHERE di.invocation_id IS p.invocation_id
                                  AND di.run_id IS COALESCE(NEW.run_id, NEW.result_run_id)))
                          )
                    );
                END
                """
            ),
            (
                "table",
                "display_overrides",
                "display_overrides",
                """
                CREATE TABLE display_overrides (
                    override_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NULL REFERENCES projects(project_id),
                    task_id TEXT NULL REFERENCES tasks(task_id),
                    run_id TEXT NULL REFERENCES task_runs(run_id),
                    invocation_id TEXT NULL REFERENCES delegation_invocations(invocation_id),
                    source_instance_id TEXT NULL,
                    session_id TEXT NULL,
                    display_name TEXT NULL,
                    hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)),
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    CHECK (
                        (project_id IS NOT NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL)
                        OR (project_id IS NULL AND task_id IS NOT NULL AND run_id IS NULL AND invocation_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL)
                        OR (project_id IS NULL AND task_id IS NULL AND run_id IS NOT NULL AND invocation_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL)
                        OR (project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NOT NULL AND source_instance_id IS NULL AND session_id IS NULL)
                        OR (project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND source_instance_id IS NOT NULL AND session_id IS NOT NULL)),
                    CHECK (display_name IS NULL OR (typeof(display_name) = 'text' AND length(display_name) BETWEEN 1 AND 500 AND instr(display_name, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ux_display_overrides_project",
                "display_overrides",
                """
                CREATE UNIQUE INDEX ux_display_overrides_project
                    ON display_overrides(project_id)
                    WHERE project_id IS NOT NULL
                """
            ),
            (
                "index",
                "ux_display_overrides_task",
                "display_overrides",
                """
                CREATE UNIQUE INDEX ux_display_overrides_task
                    ON display_overrides(task_id)
                    WHERE task_id IS NOT NULL
                """
            ),
            (
                "index",
                "ux_display_overrides_run",
                "display_overrides",
                """
                CREATE UNIQUE INDEX ux_display_overrides_run
                    ON display_overrides(run_id)
                    WHERE run_id IS NOT NULL
                """
            ),
            (
                "index",
                "ux_display_overrides_invocation",
                "display_overrides",
                """
                CREATE UNIQUE INDEX ux_display_overrides_invocation
                    ON display_overrides(invocation_id)
                    WHERE invocation_id IS NOT NULL
                """
            ),
            (
                "index",
                "ux_display_overrides_session",
                "display_overrides",
                """
                CREATE UNIQUE INDEX ux_display_overrides_session
                    ON display_overrides(source_instance_id, session_id)
                    WHERE session_id IS NOT NULL
                """
            ),
            (
                "table",
                "enrichment_imports",
                "enrichment_imports",
                """
                CREATE TABLE enrichment_imports (
                    import_id TEXT NOT NULL PRIMARY KEY,
                    source_kind TEXT NOT NULL CHECK (source_kind IN ('forge', 'opencode', 'manual', 'other')),
                    source_path TEXT NULL,
                    content_sha256 TEXT NOT NULL,
                    imported_utc TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN ('pending', 'applied', 'failed', 'unknown')),
                    UNIQUE (source_kind, content_sha256),
                    CHECK (typeof(import_id) = 'text' AND length(import_id) BETWEEN 1 AND 200 AND instr(import_id, char(0)) = 0),
                    CHECK (typeof(content_sha256) = 'text' AND length(content_sha256) = 64 AND content_sha256 GLOB '[0-9a-fA-F]*' AND instr(content_sha256, char(0)) = 0),
                    CHECK (source_path IS NULL OR length(source_path) <= 4096)
                )
                """
            ),
            (
                "index",
                "ix_enrichment_imports_imported",
                "enrichment_imports",
                """
                CREATE INDEX ix_enrichment_imports_imported ON enrichment_imports(imported_utc)
                """
            ),
            (
                "index",
                "ux_enrichment_imports_id_status",
                "enrichment_imports",
                """
                CREATE UNIQUE INDEX ux_enrichment_imports_id_status
                    ON enrichment_imports(import_id, status)
                """
            ),
            (
                "table",
                "provenance",
                "provenance",
                """
                CREATE TABLE provenance (
                    provenance_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_type TEXT NOT NULL CHECK (entity_type IN ('project', 'task', 'run', 'invocation', 'session', 'message', 'result', 'job', 'usage', 'logical_event')),
                    entity_id TEXT NOT NULL,
                    project_id TEXT NULL REFERENCES projects(project_id),
                    task_id TEXT NULL REFERENCES tasks(task_id),
                    run_id TEXT NULL REFERENCES task_runs(run_id),
                    invocation_id TEXT NULL REFERENCES delegation_invocations(invocation_id),
                    job_id TEXT NULL REFERENCES jobs(job_id),
                    usage_id TEXT NULL REFERENCES usage_observations(usage_id),
                    logical_event_id INTEGER NULL REFERENCES logical_events(logical_event_id),
                    result_id TEXT NULL REFERENCES results(result_id),
                    source_instance_id TEXT NULL,
                    session_id TEXT NULL,
                    message_id TEXT NULL,
                    source_kind TEXT NOT NULL,
                    source_ref TEXT NULL,
                    import_id TEXT NULL REFERENCES enrichment_imports(import_id),
                    observed_utc TEXT NOT NULL,
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    FOREIGN KEY (source_instance_id, session_id, message_id)
                        REFERENCES messages(source_instance_id, session_id, message_id),
                    CHECK (typeof(entity_id) = 'text' AND length(entity_id) BETWEEN 1 AND 200 AND instr(entity_id, char(0)) = 0),
                    CHECK (typeof(source_kind) = 'text' AND length(source_kind) BETWEEN 1 AND 100 AND instr(source_kind, char(0)) = 0),
                    CHECK (source_ref IS NULL OR (typeof(source_ref) = 'text' AND length(source_ref) <= 2048 AND instr(source_ref, char(0)) = 0)),
                    CHECK (
                        (entity_type = 'project' AND project_id IS NOT NULL AND project_id IS entity_id AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'task' AND task_id IS NOT NULL AND task_id IS entity_id AND project_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'run' AND run_id IS NOT NULL AND run_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'invocation' AND invocation_id IS NOT NULL AND invocation_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'job' AND job_id IS NOT NULL AND job_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'usage' AND usage_id IS NOT NULL AND usage_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'logical_event' AND logical_event_id IS NOT NULL AND entity_id GLOB '[0-9]*' AND entity_id NOT GLOB '*[^0-9]*' AND logical_event_id IS CAST(entity_id AS INTEGER) AND entity_id = CAST(logical_event_id AS TEXT) AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND result_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'result' AND result_id IS NOT NULL AND result_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND source_instance_id IS NULL AND session_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'session' AND source_instance_id IS NOT NULL AND session_id IS NOT NULL AND session_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL AND message_id IS NULL)
                        OR (entity_type = 'message' AND source_instance_id IS NOT NULL AND session_id IS NOT NULL AND message_id IS NOT NULL AND message_id IS entity_id AND project_id IS NULL AND task_id IS NULL AND run_id IS NULL AND invocation_id IS NULL AND job_id IS NULL AND usage_id IS NULL AND logical_event_id IS NULL AND result_id IS NULL)),
                    CHECK (source_instance_id IS NULL OR (typeof(source_instance_id) = 'text' AND length(source_instance_id) BETWEEN 1 AND 200 AND instr(source_instance_id, char(0)) = 0)),
                    CHECK (session_id IS NULL OR (typeof(session_id) = 'text' AND length(session_id) BETWEEN 1 AND 200 AND instr(session_id, char(0)) = 0)),
                    CHECK (message_id IS NULL OR (typeof(message_id) = 'text' AND length(message_id) BETWEEN 1 AND 200 AND instr(message_id, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ux_provenance_identity",
                "provenance",
                """
                CREATE UNIQUE INDEX ux_provenance_identity
                    ON provenance(
                        entity_type,
                        entity_id,
                        COALESCE(source_instance_id, ''),
                        COALESCE(session_id, ''),
                        source_kind,
                        COALESCE(import_id, -1))
                """
            ),
            (
                "index",
                "ix_provenance_entity",
                "provenance",
                """
                CREATE INDEX ix_provenance_entity ON provenance(entity_type, entity_id)
                """
            ),
            (
                "index",
                "ix_provenance_import",
                "provenance",
                """
                CREATE INDEX ix_provenance_import ON provenance(import_id)
                """
            ),
            (
                "trigger",
                "trg_provenance_subject_immutable",
                "provenance",
                """
                CREATE TRIGGER trg_provenance_subject_immutable
                BEFORE UPDATE OF
                    entity_type, entity_id, project_id, task_id, run_id,
                    invocation_id, job_id, usage_id, logical_event_id, result_id,
                    source_instance_id, session_id, message_id
                ON provenance
                BEGIN
                    SELECT RAISE(ABORT, 'provenance subject immutable');
                END
                """
            ),
            // Guard both INSERT and UPDATE on the mutable parents: REPLACE can
            // retain a referenced identity while replacing its ownership, without
            // firing UPDATE (or, by default, DELETE) triggers on that parent.
            // AFTER INSERT allows conflict no-ops and ownership-preserving UPSERTs;
            // ABORT still rolls back an incompatible replacement in its entirety.
            (
                "trigger",
                "trg_task_runs_validation_reviewer_insert",
                "task_runs",
                """
                CREATE TRIGGER trg_task_runs_validation_reviewer_insert
                AFTER INSERT ON task_runs
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        JOIN provenance p ON p.provenance_id = v.reviewer_provenance_id
                        WHERE v.is_verified = 1 AND p.entity_type = 'job'
                          AND COALESCE(v.run_id, v.result_run_id) IS NEW.run_id
                          AND p.job_id IS NOT NEW.job_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_task_runs_validation_reviewer_update",
                "task_runs",
                """
                CREATE TRIGGER trg_task_runs_validation_reviewer_update
                BEFORE UPDATE OF run_id, job_id ON task_runs
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        JOIN provenance p ON p.provenance_id = v.reviewer_provenance_id
                        WHERE v.is_verified = 1 AND p.entity_type = 'job'
                          AND COALESCE(v.run_id, v.result_run_id) IS NEW.run_id
                          AND p.job_id IS NOT NEW.job_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_invocations_validation_reviewer_insert",
                "delegation_invocations",
                """
                CREATE TRIGGER trg_invocations_validation_reviewer_insert
                AFTER INSERT ON delegation_invocations
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        JOIN provenance p ON p.provenance_id = v.reviewer_provenance_id
                        WHERE v.is_verified = 1 AND p.entity_type = 'invocation'
                          AND p.invocation_id IS NEW.invocation_id
                          AND COALESCE(v.run_id, v.result_run_id) IS NOT NEW.run_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_invocations_validation_reviewer_update",
                "delegation_invocations",
                """
                CREATE TRIGGER trg_invocations_validation_reviewer_update
                BEFORE UPDATE OF invocation_id, run_id ON delegation_invocations
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        JOIN provenance p ON p.provenance_id = v.reviewer_provenance_id
                        WHERE v.is_verified = 1 AND p.entity_type = 'invocation'
                          AND p.invocation_id IS NEW.invocation_id
                          AND COALESCE(v.run_id, v.result_run_id) IS NOT NEW.run_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_provenance_validation_reviewer_insert",
                "provenance",
                """
                CREATE TRIGGER trg_provenance_validation_reviewer_insert
                AFTER INSERT ON provenance
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        WHERE v.is_verified = 1 AND v.reviewer_provenance_id = NEW.provenance_id
                          AND NOT (
                            (NEW.entity_type = 'result' AND NEW.result_id IS NOT NULL AND NEW.result_id IS v.result_id)
                            OR (NEW.entity_type = 'run' AND NEW.run_id IS NOT NULL AND NEW.run_id IS COALESCE(v.run_id, v.result_run_id))
                            OR (NEW.entity_type = 'job' AND NEW.job_id IS NOT NULL AND NEW.job_id IS (
                                SELECT r.job_id FROM task_runs r WHERE r.run_id IS COALESCE(v.run_id, v.result_run_id)))
                            OR (NEW.entity_type = 'invocation' AND NEW.invocation_id IS NOT NULL AND EXISTS (
                                SELECT 1 FROM delegation_invocations di
                                WHERE di.invocation_id IS NEW.invocation_id AND di.run_id IS COALESCE(v.run_id, v.result_run_id)))
                          )
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_provenance_validation_reviewer_update",
                "provenance",
                """
                CREATE TRIGGER trg_provenance_validation_reviewer_update
                BEFORE UPDATE OF provenance_id ON provenance
                BEGIN
                    SELECT RAISE(ABORT, 'validation reviewer provenance mismatch')
                    WHERE EXISTS (
                        SELECT 1 FROM validations v
                        WHERE v.is_verified = 1 AND v.reviewer_provenance_id = NEW.provenance_id
                          AND NOT (
                            (NEW.entity_type = 'result' AND NEW.result_id IS NOT NULL AND NEW.result_id IS v.result_id)
                            OR (NEW.entity_type = 'run' AND NEW.run_id IS NOT NULL AND NEW.run_id IS COALESCE(v.run_id, v.result_run_id))
                            OR (NEW.entity_type = 'job' AND NEW.job_id IS NOT NULL AND NEW.job_id IS (
                                SELECT r.job_id FROM task_runs r WHERE r.run_id IS COALESCE(v.run_id, v.result_run_id)))
                            OR (NEW.entity_type = 'invocation' AND NEW.invocation_id IS NOT NULL AND EXISTS (
                                SELECT 1 FROM delegation_invocations di
                                WHERE di.invocation_id IS NEW.invocation_id AND di.run_id IS COALESCE(v.run_id, v.result_run_id)))
                          )
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_jobs_provenance_subject_insert",
                "jobs",
                """
                CREATE TRIGGER trg_jobs_provenance_subject_insert
                BEFORE INSERT ON jobs
                WHEN NEW.provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'provenance subject mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.provenance_id
                          AND p.entity_type = 'job'
                          AND p.job_id = NEW.job_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_jobs_provenance_subject_update",
                "jobs",
                """
                CREATE TRIGGER trg_jobs_provenance_subject_update
                BEFORE UPDATE OF provenance_id ON jobs
                WHEN NEW.provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'provenance subject mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.provenance_id
                          AND p.entity_type = 'job'
                          AND p.job_id = NEW.job_id
                    );
                END
                """
            ),
            (
                "table",
                "run_enrichment",
                "run_enrichment",
                """
                CREATE TABLE run_enrichment (
                    run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                    import_id TEXT NOT NULL,
                    import_status TEXT NOT NULL CHECK (import_status = 'applied'),
                    enrichment_kind TEXT NOT NULL CHECK (enrichment_kind IN ('metadata', 'usage', 'cost', 'result', 'other')),
                    applied_utc TEXT NOT NULL,
                    PRIMARY KEY (run_id, import_id, enrichment_kind),
                    FOREIGN KEY (import_id, import_status)
                        REFERENCES enrichment_imports(import_id, status)
                )
                """
            ),
            (
                "index",
                "ix_run_enrichment_import",
                "run_enrichment",
                """
                CREATE INDEX ix_run_enrichment_import ON run_enrichment(import_id)
                """
            ),
            (
                "table",
                "usage_observations",
                "usage_observations",
                """
                CREATE TABLE usage_observations (
                    usage_id TEXT NOT NULL PRIMARY KEY,
                    source_instance_id TEXT NOT NULL,
                    source_usage_id TEXT NOT NULL,
                    usage_scope TEXT NOT NULL CHECK (usage_scope IN ('message', 'session', 'invocation', 'run')),
                    authority TEXT NOT NULL CHECK (authority IN ('reported', 'derived', 'estimated')),
                    provenance_id INTEGER NULL REFERENCES provenance(provenance_id),
                    logical_event_id INTEGER NULL,
                    logical_event_decision TEXT NULL,
                    logical_event_cost_text TEXT NULL,
                    run_id TEXT NULL,
                    run_association_role TEXT NULL,
                    invocation_id TEXT NULL,
                    invocation_run_id TEXT NULL,
                    invocation_run_association_role TEXT NULL,
                    session_id TEXT NULL,
                    message_id TEXT NULL,
                    observed_utc TEXT NOT NULL,
                    input_tokens INTEGER NULL CHECK (input_tokens IS NULL OR (typeof(input_tokens) = 'integer' AND input_tokens >= 0)),
                    output_tokens INTEGER NULL CHECK (output_tokens IS NULL OR (typeof(output_tokens) = 'integer' AND output_tokens >= 0)),
                    cache_read_tokens INTEGER NULL CHECK (cache_read_tokens IS NULL OR (typeof(cache_read_tokens) = 'integer' AND cache_read_tokens >= 0)),
                    cache_write_tokens INTEGER NULL CHECK (cache_write_tokens IS NULL OR (typeof(cache_write_tokens) = 'integer' AND cache_write_tokens >= 0)),
                    cost_text TEXT NULL,
                    cost_currency TEXT NULL,
                    is_estimated INTEGER NOT NULL DEFAULT 0 CHECK (is_estimated IN (0, 1)),
                    UNIQUE (source_instance_id, source_usage_id),
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    FOREIGN KEY (source_instance_id, session_id, message_id)
                        REFERENCES messages(source_instance_id, session_id, message_id),
                    FOREIGN KEY (logical_event_id, logical_event_decision, logical_event_cost_text)
                        REFERENCES logical_events(logical_event_id, decision, cost_text),
                    FOREIGN KEY (run_id, run_association_role)
                        REFERENCES task_runs(run_id, association_role),
                    FOREIGN KEY (invocation_id, invocation_run_id)
                        REFERENCES delegation_invocations(invocation_id, run_id),
                    FOREIGN KEY (invocation_run_id, invocation_run_association_role)
                        REFERENCES task_runs(run_id, association_role),
                    CHECK (typeof(usage_id) = 'text' AND length(usage_id) BETWEEN 1 AND 200 AND instr(usage_id, char(0)) = 0),
                    CHECK (typeof(source_instance_id) = 'text' AND length(source_instance_id) BETWEEN 1 AND 200 AND instr(source_instance_id, char(0)) = 0),
                    CHECK (typeof(source_usage_id) = 'text' AND length(source_usage_id) BETWEEN 1 AND 200 AND instr(source_usage_id, char(0)) = 0),
                    CHECK ((logical_event_id IS NULL AND logical_event_decision IS NULL AND logical_event_cost_text IS NULL)
                        OR (logical_event_id IS NOT NULL AND logical_event_decision IS NOT NULL AND logical_event_cost_text IS NOT NULL)),
                    CHECK (logical_event_decision IS NULL OR logical_event_decision = 'Applied'),
                    CHECK (logical_event_cost_text IS NULL OR (length(logical_event_cost_text) BETWEEN 1 AND 64 AND logical_event_cost_text GLOB '[0-9]*' AND logical_event_cost_text NOT GLOB '*[^0-9.]*' AND logical_event_cost_text NOT GLOB '*.*.*' AND logical_event_cost_text NOT GLOB '.*' AND logical_event_cost_text NOT GLOB '*.')),
                    CHECK ((run_id IS NULL AND run_association_role IS NULL)
                        OR (run_id IS NOT NULL AND run_association_role IS NOT NULL)),
                    CHECK ((invocation_id IS NULL AND invocation_run_id IS NULL AND invocation_run_association_role IS NULL)
                        OR (invocation_id IS NOT NULL AND invocation_run_id IS NOT NULL AND invocation_run_association_role IS NOT NULL)),
                    CHECK (
                        (usage_scope = 'message' AND message_id IS NOT NULL AND session_id IS NOT NULL AND run_id IS NULL AND invocation_id IS NULL)
                        OR (usage_scope = 'session' AND session_id IS NOT NULL AND message_id IS NULL AND run_id IS NULL AND invocation_id IS NULL)
                        OR (usage_scope = 'run' AND run_id IS NOT NULL AND message_id IS NULL AND session_id IS NULL AND invocation_id IS NULL)
                        OR (usage_scope = 'invocation' AND invocation_id IS NOT NULL AND message_id IS NULL AND session_id IS NULL AND run_id IS NULL)),
                    CHECK (message_id IS NULL OR session_id IS NOT NULL),
                    CHECK (cost_text IS NULL OR (typeof(cost_text) = 'text' AND instr(cost_text, char(0)) = 0 AND length(cost_text) BETWEEN 1 AND 64 AND cost_text GLOB '[0-9]*' AND cost_text NOT GLOB '*[^0-9.]*' AND cost_text NOT GLOB '*.*.*' AND cost_text NOT GLOB '.*' AND cost_text NOT GLOB '*.'
                        AND length(replace(cost_text, '.', '')) <= 29
                        AND (instr(cost_text, '.') = 0 OR length(cost_text) - instr(cost_text, '.') <= 28))),
                    CHECK (cost_currency IS NULL OR (typeof(cost_currency) = 'text' AND cost_currency GLOB '[A-Z][A-Z][A-Z]' AND instr(cost_currency, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ix_usage_observations_source",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_source
                    ON usage_observations(source_instance_id, source_usage_id)
                """
            ),
            (
                "index",
                "ix_usage_observations_provenance",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_provenance
                    ON usage_observations(provenance_id)
                """
            ),
            (
                "index",
                "ix_usage_observations_logical_event",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_logical_event
                    ON usage_observations(logical_event_id)
                """
            ),
            (
                "index",
                "ix_usage_observations_run",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_run
                    ON usage_observations(run_id, observed_utc)
                """
            ),
            (
                "index",
                "ix_usage_observations_invocation",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_invocation
                    ON usage_observations(invocation_id)
                """
            ),
            (
                "index",
                "ix_usage_observations_session",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_session
                    ON usage_observations(source_instance_id, session_id, observed_utc)
                """
            ),
            (
                "index",
                "ix_usage_observations_message",
                "usage_observations",
                """
                CREATE INDEX ix_usage_observations_message
                    ON usage_observations(source_instance_id, session_id, message_id)
                """
            ),
            (
                "trigger",
                "trg_usage_provenance_subject_insert",
                "usage_observations",
                """
                CREATE TRIGGER trg_usage_provenance_subject_insert
                BEFORE INSERT ON usage_observations
                WHEN NEW.provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'provenance subject mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.provenance_id
                          AND p.entity_type = 'usage'
                          AND p.usage_id = NEW.usage_id
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_usage_provenance_subject_update",
                "usage_observations",
                """
                CREATE TRIGGER trg_usage_provenance_subject_update
                BEFORE UPDATE OF provenance_id ON usage_observations
                WHEN NEW.provenance_id IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'provenance subject mismatch')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM provenance p
                        WHERE p.provenance_id = NEW.provenance_id
                          AND p.entity_type = 'usage'
                          AND p.usage_id = NEW.usage_id
                    );
                END
                """
            ),
            (
                "table",
                "cost_attributions",
                "cost_attributions",
                """
                CREATE TABLE cost_attributions (
                    attribution_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    usage_id TEXT NOT NULL REFERENCES usage_observations(usage_id),
                    logical_event_id INTEGER NOT NULL,
                    logical_event_decision TEXT NOT NULL CHECK (logical_event_decision = 'Applied'),
                    logical_event_cost_text TEXT NOT NULL,
                    attribution_role TEXT NOT NULL CHECK (attribution_role IN ('primary', 'secondary', 'overhead')),
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                    allocated_utc TEXT NOT NULL,
                    FOREIGN KEY (logical_event_id, logical_event_decision, logical_event_cost_text)
                        REFERENCES logical_events(logical_event_id, decision, cost_text),
                    CHECK (length(usage_id) BETWEEN 1 AND 200 AND instr(usage_id, char(0)) = 0),
                    CHECK (logical_event_cost_text GLOB '[0-9]*'
                        AND logical_event_cost_text NOT GLOB '*[^0-9.]*'
                        AND logical_event_cost_text NOT GLOB '*.*.*'
                        AND logical_event_cost_text NOT GLOB '.*'
                        AND logical_event_cost_text NOT GLOB '*.'
                        AND length(replace(logical_event_cost_text, '.', '')) <= 29
                        AND (instr(logical_event_cost_text, '.') = 0
                            OR length(logical_event_cost_text) - instr(logical_event_cost_text, '.') <= 28))
                )
                """
            ),
            (
                "index",
                "ux_cost_attributions_active_usage",
                "cost_attributions",
                """
                CREATE UNIQUE INDEX ux_cost_attributions_active_usage
                    ON cost_attributions(usage_id)
                    WHERE is_active = 1
                """
            ),
            (
                "index",
                "ux_cost_attributions_active_event",
                "cost_attributions",
                """
                CREATE UNIQUE INDEX ux_cost_attributions_active_event
                    ON cost_attributions(logical_event_id)
                    WHERE is_active = 1
                """
            ),
            (
                "index",
                "ix_cost_attributions_usage",
                "cost_attributions",
                """
                CREATE INDEX ix_cost_attributions_usage ON cost_attributions(usage_id)
                """
            ),
            (
                "index",
                "ix_cost_attributions_logical_event",
                "cost_attributions",
                """
                CREATE INDEX ix_cost_attributions_logical_event
                    ON cost_attributions(logical_event_id)
                """
            ),
            (
                "table",
                "monitoring_starts",
                "monitoring_starts",
                """
                CREATE TABLE monitoring_starts (
                    start_id TEXT NOT NULL PRIMARY KEY,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    log_path TEXT NULL,
                    jsonl_path TEXT NULL,
                    status TEXT NOT NULL CHECK (status IN ('running', 'stopped', 'failed')),
                    UNIQUE (log_path),
                    UNIQUE (jsonl_path),
                    CHECK (typeof(start_id) = 'text' AND length(start_id) BETWEEN 1 AND 200 AND instr(start_id, char(0)) = 0),
                    CHECK (ended_utc IS NULL OR ended_utc >= started_utc),
                    CHECK (log_path IS NULL OR length(log_path) <= 4096),
                    CHECK (jsonl_path IS NULL OR length(jsonl_path) <= 4096)
                )
                """
            ),
            (
                "index",
                "ix_monitoring_starts_started",
                "monitoring_starts",
                """
                CREATE INDEX ix_monitoring_starts_started ON monitoring_starts(started_utc)
                """
            ),
            (
                "table",
                "source_delivery_cuts",
                "source_delivery_cuts",
                """
                CREATE TABLE source_delivery_cuts (
                    cut_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_id TEXT NOT NULL REFERENCES monitoring_starts(start_id),
                    job_id TEXT NOT NULL REFERENCES jobs(job_id),
                    generation_id INTEGER NOT NULL,
                    committed_offset INTEGER NOT NULL,
                    delivery_kind TEXT NOT NULL CHECK (delivery_kind IN ('log', 'jsonl')),
                    delivered_offset INTEGER NOT NULL CHECK (delivered_offset >= 0),
                    cut_utc TEXT NOT NULL,
                    UNIQUE (start_id, delivery_kind, generation_id),
                    FOREIGN KEY (generation_id, job_id)
                        REFERENCES source_generations(generation_id, job_id),
                    CHECK (typeof(start_id) = 'text' AND length(start_id) BETWEEN 1 AND 200 AND instr(start_id, char(0)) = 0),
                    CHECK (typeof(job_id) = 'text' AND length(job_id) BETWEEN 1 AND 200 AND instr(job_id, char(0)) = 0),
                    CHECK (delivered_offset <= committed_offset)
                )
                """
            ),
            (
                "index",
                "ix_source_delivery_cuts_generation",
                "source_delivery_cuts",
                """
                CREATE INDEX ix_source_delivery_cuts_generation
                    ON source_delivery_cuts(generation_id)
                """
            ),
            (
                "index",
                "ix_source_delivery_cuts_start",
                "source_delivery_cuts",
                """
                CREATE INDEX ix_source_delivery_cuts_start
                    ON source_delivery_cuts(start_id, delivery_kind)
                """
            ),
            (
                "trigger",
                "trg_source_delivery_cuts_capture_insert",
                "source_delivery_cuts",
                """
                CREATE TRIGGER trg_source_delivery_cuts_capture_insert
                BEFORE INSERT ON source_delivery_cuts
                BEGIN
                    SELECT RAISE(ABORT, 'delivery cut capture does not match committed cursor')
                    WHERE NOT EXISTS (
                        SELECT 1 FROM source_cursors c
                        WHERE c.generation_id = NEW.generation_id
                          AND c.committed_offset = NEW.committed_offset
                    );
                END
                """
            ),
            (
                "trigger",
                "trg_source_delivery_cuts_capture_immutable_update",
                "source_delivery_cuts",
                """
                CREATE TRIGGER trg_source_delivery_cuts_capture_immutable_update
                BEFORE UPDATE OF start_id, job_id, generation_id, committed_offset, delivery_kind
                ON source_delivery_cuts
                BEGIN
                    SELECT RAISE(ABORT, 'delivery cut snapshot immutable');
                END
                """
            ),
            (
                "table",
                "event_observation_context",
                "event_observation_context",
                """
                CREATE TABLE event_observation_context (
                    observation_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    logical_event_id INTEGER NOT NULL REFERENCES logical_events(logical_event_id),
                    start_id TEXT NULL REFERENCES monitoring_starts(start_id),
                    project_id TEXT NULL REFERENCES projects(project_id),
                    source_instance_id TEXT NULL,
                    session_id TEXT NULL,
                    observed_utc TEXT NOT NULL,
                    context_kind TEXT NOT NULL CHECK (context_kind IN ('bootstrap', 'live', 'enrichment', 'manual')),
                    detail TEXT NULL,
                    FOREIGN KEY (source_instance_id, session_id)
                        REFERENCES opencode_sessions(source_instance_id, session_id),
                    CHECK (session_id IS NULL OR source_instance_id IS NOT NULL),
                    CHECK (context_kind <> 'live' OR start_id IS NOT NULL),
                    CHECK (detail IS NULL OR (typeof(detail) = 'text' AND length(detail) <= 4096 AND instr(detail, char(0)) = 0))
                )
                """
            ),
            (
                "index",
                "ux_event_observation_context_identity",
                "event_observation_context",
                """
                CREATE UNIQUE INDEX ux_event_observation_context_identity
                    ON event_observation_context(
                        logical_event_id,
                        COALESCE(start_id, ''),
                        COALESCE(source_instance_id, ''),
                        COALESCE(session_id, ''),
                        context_kind)
                """
            ),
            (
                "index",
                "ix_event_observation_context_event",
                "event_observation_context",
                """
                CREATE INDEX ix_event_observation_context_event
                    ON event_observation_context(logical_event_id)
                """
            ),
            (
                "index",
                "ix_event_observation_context_start",
                "event_observation_context",
                """
                CREATE INDEX ix_event_observation_context_start
                    ON event_observation_context(start_id)
                """
            ),
            (
                "index",
                "ix_event_observation_context_session",
                "event_observation_context",
                """
                CREATE INDEX ix_event_observation_context_session
                    ON event_observation_context(source_instance_id, session_id)
                """
            )
        };
    }

    private static readonly (string Type, string Name, string Table, string? Sql)[]
        V3InternalSchemaObjects =
        {
            (
                "table",
                "sqlite_sequence",
                "sqlite_sequence",
                "CREATE TABLE sqlite_sequence(name,seq)"
            ),
            ("index", "sqlite_autoindex_content_items_1", "content_items", null),
            ("index", "sqlite_autoindex_delegation_invocations_1", "delegation_invocations", null),
            ("index", "sqlite_autoindex_delegation_invocations_2", "delegation_invocations", null),
            ("index", "sqlite_autoindex_enrichment_imports_1", "enrichment_imports", null),
            ("index", "sqlite_autoindex_enrichment_imports_2", "enrichment_imports", null),
            ("index", "sqlite_autoindex_execution_evidence_1", "execution_evidence", null),
            ("index", "sqlite_autoindex_execution_segments_1", "execution_segments", null),
            ("index", "sqlite_autoindex_execution_segments_2", "execution_segments", null),
            ("index", "sqlite_autoindex_execution_segments_3", "execution_segments", null),
            ("index", "sqlite_autoindex_invocation_jobs_1", "invocation_jobs", null),
            ("index", "sqlite_autoindex_job_aggregates_1", "job_aggregates", null),
            ("index", "sqlite_autoindex_job_coverage_1", "job_coverage", null),
            ("index", "sqlite_autoindex_job_logical_keys_1", "job_logical_keys", null),
            ("index", "sqlite_autoindex_job_sources_1", "job_sources", null),
            ("index", "sqlite_autoindex_jobs_1", "jobs", null),
            ("index", "sqlite_autoindex_ledger_meta_1", "ledger_meta", null),
            ("index", "sqlite_autoindex_logical_events_1", "logical_events", null),
            ("index", "sqlite_autoindex_messages_1", "messages", null),
            ("index", "sqlite_autoindex_messages_2", "messages", null),
            ("index", "sqlite_autoindex_monitoring_starts_1", "monitoring_starts", null),
            ("index", "sqlite_autoindex_monitoring_starts_2", "monitoring_starts", null),
            ("index", "sqlite_autoindex_monitoring_starts_3", "monitoring_starts", null),
            ("index", "sqlite_autoindex_opencode_sessions_1", "opencode_sessions", null),
            ("index", "sqlite_autoindex_project_paths_1", "project_paths", null),
            ("index", "sqlite_autoindex_project_paths_2", "project_paths", null),
            ("index", "sqlite_autoindex_projects_1", "projects", null),
            ("index", "sqlite_autoindex_results_1", "results", null),
            ("index", "sqlite_autoindex_run_enrichment_1", "run_enrichment", null),
            ("index", "sqlite_autoindex_run_segments_1", "run_segments", null),
            ("index", "sqlite_autoindex_run_segments_2", "run_segments", null),
            ("index", "sqlite_autoindex_segment_messages_1", "segment_messages", null),
            ("index", "sqlite_autoindex_segment_messages_2", "segment_messages", null),
            ("index", "sqlite_autoindex_source_delivery_cuts_1", "source_delivery_cuts", null),
            ("index", "sqlite_autoindex_source_events_1", "source_events", null),
            ("index", "sqlite_autoindex_source_records_1", "source_records", null),
            ("index", "sqlite_autoindex_task_runs_1", "task_runs", null),
            ("index", "sqlite_autoindex_task_runs_2", "task_runs", null),
            ("index", "sqlite_autoindex_tasks_1", "tasks", null),
            ("index", "sqlite_autoindex_usage_observations_1", "usage_observations", null),
            ("index", "sqlite_autoindex_usage_observations_2", "usage_observations", null),
            ("index", "sqlite_autoindex_validations_1", "validations", null)
        };

    private static readonly (string Type, string Name, string Table, string? Sql)[]
        ProjectionInternalSchemaObjects =
        {
            (
                "table",
                "sqlite_sequence",
                "sqlite_sequence",
                "CREATE TABLE sqlite_sequence(name,seq)"
            ),
            ("index", "sqlite_autoindex_job_aggregates_1", "job_aggregates", null),
            ("index", "sqlite_autoindex_job_coverage_1", "job_coverage", null),
            ("index", "sqlite_autoindex_job_logical_keys_1", "job_logical_keys", null),
            ("index", "sqlite_autoindex_job_sources_1", "job_sources", null),
            ("index", "sqlite_autoindex_jobs_1", "jobs", null),
            ("index", "sqlite_autoindex_ledger_meta_1", "ledger_meta", null),
            ("index", "sqlite_autoindex_logical_events_1", "logical_events", null),
            ("index", "sqlite_autoindex_source_events_1", "source_events", null),
            ("index", "sqlite_autoindex_source_records_1", "source_records", null)
        };

    private sealed class EventLedgerSchemaProfile
    {
        private EventLedgerSchemaProfile(
            int version,
            IReadOnlyList<(string Type, string Name, string Table, string Sql)> requiredObjects,
            IReadOnlyList<(string Type, string Name, string Table, string? Sql)> internalObjects,
            bool supportsProjection)
        {
            Version = version;
            RequiredObjects = requiredObjects;
            InternalObjects = internalObjects;
            SupportsProjection = supportsProjection;
        }

        public static EventLedgerSchemaProfile V1 { get; } = new(
            EventLedgerSchema.CurrentVersion,
            RequiredSchemaObjects,
            ExpectedInternalSchemaObjects,
            supportsProjection: false);

        public static EventLedgerSchemaProfile Projection { get; } = new(
            EventLedgerSchema.ProjectionVersion,
            ProjectionRequiredSchemaObjects,
            ProjectionInternalSchemaObjects,
            supportsProjection: true);

        public static EventLedgerSchemaProfile V3 { get; } = new(
            EventLedgerSchema.V3Version,
            V3RequiredSchemaObjects,
            V3InternalSchemaObjects,
            supportsProjection: true);

        public int Version { get; }

        public IReadOnlyList<(string Type, string Name, string Table, string Sql)> RequiredObjects { get; }

        public IReadOnlyList<(string Type, string Name, string Table, string? Sql)> InternalObjects { get; }

        public bool SupportsProjection { get; }
    }

    private void InitializeSchema()
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            foreach (var schemaObject in _schema.RequiredObjects)
            {
                ExecuteNonQuery(transaction, schemaObject.Sql);
            }

            ExecuteNonQuery(
                transaction,
                "INSERT INTO ledger_meta(key, value) VALUES ('schema_version', $version);",
                ("$version", _schema.Version.ToString(CultureInfo.InvariantCulture)));
            ExecuteNonQuery(
                transaction,
                $"PRAGMA user_version = {_schema.Version.ToString(CultureInfo.InvariantCulture)};");
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
        if (!_schema.SupportsProjection)
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
            return;
        }

        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO jobs(job_id, created_utc, last_seen_utc)
            VALUES ($job, $now, $now)
            ON CONFLICT(job_id) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;
            """,
            ("$job", jobId),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));

        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO job_aggregates(job_id, updated_utc)
            VALUES ($job, $now)
            ON CONFLICT(job_id) DO NOTHING;
            """,
            ("$job", jobId),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));

        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO job_coverage(job_id, status, detail, updated_utc)
            VALUES ($job, $status, $detail, $now)
            ON CONFLICT(job_id) DO NOTHING;
            """,
            ("$job", jobId),
            ("$status", LedgerCoverageState.Clean.ToString()),
            ("$detail", "Historical coverage has not been evaluated yet."),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));
    }

    /// <summary>
    /// Registers a discovered durable job and its verified runs root. The
    /// job_id is the stable forge metadata identity; a conflicting runs root
    /// or metadata path is surfaced as incomplete coverage instead of being
    /// silently merged into an unrelated job.
    /// </summary>
    public bool RegisterJob(
        string jobId,
        string runsRoot,
        string? metadataPath,
        DateTimeOffset? observedAt = null,
        bool provisional = false)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        if (string.IsNullOrWhiteSpace(runsRoot))
        {
            throw new ArgumentException("A runs root is required.", nameof(runsRoot));
        }

        var now = observedAt ?? DateTimeOffset.UtcNow;
        var normalizedRoot = Path.GetFullPath(runsRoot);
        var normalizedMetadata = string.IsNullOrWhiteSpace(metadataPath)
            ? null
            : Path.GetFullPath(metadataPath);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_schema.SupportsProjection)
            {
                throw new InvalidOperationException(
                    "Durable job registration requires the B2 projection schema.");
            }

            _storageGuard.PrepareAndValidate();
            SqliteTransaction? transaction = null;
            var conflict = false;
            try
            {
                transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                ValidateWriteTransaction(transaction);
                EnsureJob(jobId, now, transaction);

                var existingRoot = ExecuteScalar(
                    transaction,
                    "SELECT runs_root FROM jobs WHERE job_id = $job;",
                    ("$job", jobId)) as string;
                var existingMetadata = ExecuteScalar(
                    transaction,
                    "SELECT metadata_path FROM jobs WHERE job_id = $job;",
                    ("$job", jobId)) as string;

                conflict =
                    (!string.IsNullOrWhiteSpace(existingRoot) &&
                     !string.Equals(existingRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase)) ||
                    (normalizedMetadata is not null &&
                     !string.IsNullOrWhiteSpace(existingMetadata) &&
                     !string.Equals(existingMetadata, normalizedMetadata, StringComparison.OrdinalIgnoreCase));

                ExecuteNonQuery(
                    transaction,
                    """
                    UPDATE jobs
                    SET runs_root = COALESCE(runs_root, $root),
                        metadata_path = COALESCE(metadata_path, $metadata),
                        last_seen_utc = $now,
                        provisional = $provisional
                    WHERE job_id = $job;
                    """,
                    ("$root", normalizedRoot),
                    ("$metadata", normalizedMetadata),
                    ("$now", LedgerStoragePaths.FormatTimestamp(now)),
                    ("$provisional", provisional ? 1 : 0),
                    ("$job", jobId));

                if (conflict)
                {
                    ExecuteNonQuery(
                        transaction,
                        """
                        UPDATE jobs
                        SET conflict_detail = $detail
                        WHERE job_id = $job;
                        """,
                        ("$detail", "Conflicting runs-root or metadata association for the same durable job id."),
                        ("$job", jobId));
                }

                RefreshJobCoverage(transaction, jobId, now);
                transaction.Commit();
            }
            catch (LedgerException)
            {
                TryRollback(transaction);
                throw;
            }
            catch (SqliteException ex)
            {
                TryRollback(transaction);
                throw MapSqliteException(ex, "The durable job registration failed and was rolled back.");
            }
            finally
            {
                transaction?.Dispose();
            }

            return conflict;
        }
    }

    /// <summary>Registers a verified or expected job/source association.</summary>
    public void RegisterJobSource(
        string jobId,
        string sourcePath,
        string status,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A source path is required.", nameof(sourcePath));
        }

        var now = observedAt ?? DateTimeOffset.UtcNow;
        var fullPath = Path.GetFullPath(sourcePath);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_schema.SupportsProjection)
            {
                throw new InvalidOperationException(
                    "Durable source registration requires the B2 projection schema.");
            }

            _storageGuard.PrepareAndValidate();
            SqliteTransaction? transaction = null;
            try
            {
                transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                ValidateWriteTransaction(transaction);
                EnsureJob(jobId, now, transaction);
                TouchJobSource(transaction, jobId, fullPath, status, now);
                RefreshJobCoverage(transaction, jobId, now);
                transaction.Commit();
            }
            catch (LedgerException)
            {
                TryRollback(transaction);
                throw;
            }
            catch (SqliteException ex)
            {
                TryRollback(transaction);
                throw MapSqliteException(ex, "The job/source registration failed and was rolled back.");
            }
            finally
            {
                transaction?.Dispose();
            }
        }
    }

    /// <summary>
    /// Persists an explicit job-level uncovered-source condition. Passing null
    /// clears it when the condition no longer exists, allowing coverage to be
    /// recalculated without inventing or merging source data.
    /// </summary>
    public void SetJobCoverageIncomplete(
        string jobId,
        string? detail,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_schema.SupportsProjection)
            {
                throw new InvalidOperationException(
                    "Durable coverage updates require the B2 projection schema.");
            }

            _storageGuard.PrepareAndValidate();
            SqliteTransaction? transaction = null;
            try
            {
                transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                ValidateWriteTransaction(transaction);
                EnsureJob(jobId, now, transaction);
                ExecuteNonQuery(
                    transaction,
                    """
                    UPDATE jobs
                    SET uncovered_detail = $detail,
                        last_seen_utc = $now
                    WHERE job_id = $job;
                    """,
                    ("$detail", detail),
                    ("$now", LedgerStoragePaths.FormatTimestamp(now)),
                    ("$job", jobId));
                RefreshJobCoverage(transaction, jobId, now);
                transaction.Commit();
            }
            catch (LedgerException)
            {
                TryRollback(transaction);
                throw;
            }
            catch (SqliteException ex)
            {
                TryRollback(transaction);
                throw MapSqliteException(ex, "The uncovered-source coverage update failed and was rolled back.");
            }
            finally
            {
                transaction?.Dispose();
            }
        }
    }

    public void MarkSourceUnavailable(
        string jobId,
        string sourcePath,
        DateTimeOffset? observedAt = null) =>
        RegisterJobSource(
            jobId,
            sourcePath,
            LedgerCoverageState.Unavailable.ToString(),
            observedAt);

    public void UpdateDiscoveryState(
        string runsRoot,
        bool directoryAvailable,
        IReadOnlyList<string> warnings,
        DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var serializedWarnings = string.Join('\n', warnings ?? Array.Empty<string>());

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_schema.SupportsProjection)
            {
                return;
            }

            _storageGuard.PrepareAndValidate();
            SqliteTransaction? transaction = null;
            try
            {
                transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                ValidateWriteTransaction(transaction);
                ExecuteNonQuery(
                    transaction,
                    """
                    INSERT INTO discovery_state(
                        id, runs_root, directory_available, warnings, updated_utc)
                    VALUES (1, $root, $available, $warnings, $now)
                    ON CONFLICT(id) DO UPDATE SET
                        runs_root = excluded.runs_root,
                        directory_available = excluded.directory_available,
                        warnings = excluded.warnings,
                        updated_utc = excluded.updated_utc;
                    """,
                    ("$root", Path.GetFullPath(runsRoot)),
                    ("$available", directoryAvailable ? 1 : 0),
                    ("$warnings", serializedWarnings),
                    ("$now", LedgerStoragePaths.FormatTimestamp(now)));
                transaction.Commit();
            }
            catch (LedgerException)
            {
                TryRollback(transaction);
                throw;
            }
            catch (SqliteException ex)
            {
                TryRollback(transaction);
                throw MapSqliteException(ex, "The discovery state could not be persisted.");
            }
            finally
            {
                transaction?.Dispose();
            }
        }
    }

    public LedgerJobAggregate GetJobAggregate(string jobId)
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
                return ReadAggregate(null, jobId);
            }
            catch (SqliteException ex)
            {
                throw MapSqliteException(ex, "The durable job aggregate could not be read.");
            }
        }
    }

    public LedgerCoverage GetJobCoverage(string jobId)
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
                return ReadCoverage(null, jobId);
            }
            catch (SqliteException ex)
            {
                throw MapSqliteException(ex, "The durable job coverage could not be read.");
            }
        }
    }

    public IReadOnlyList<LedgerJobSource> ListJobSources(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT job_id, source_path, status, first_seen_utc, last_seen_utc
                FROM job_sources
                WHERE job_id = $job
                ORDER BY source_path;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            var results = new List<LedgerJobSource>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LedgerJobSource(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    LedgerStoragePaths.ParseTimestamp(reader.GetString(3)),
                    LedgerStoragePaths.ParseTimestamp(reader.GetString(4))));
            }

            return results;
        }
    }

    public IReadOnlyList<LedgerBootstrapProgress> ListBootstrapProgress(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT generation_id, initial_boundary, committed_offset,
                       completed_utc, status
                FROM bootstrap_sources
                WHERE job_id = $job
                ORDER BY generation_id;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            var results = new List<LedgerBootstrapProgress>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LedgerBootstrapProgress(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3)
                        ? null
                        : LedgerStoragePaths.ParseTimestamp(reader.GetString(3)),
                    reader.GetString(4)));
            }

            return results;
        }
    }

    /// <summary>
    /// Restores a bounded timeline. All-known histories use the newest
    /// timestamped events; a history with any missing timestamp uses the most
    /// recent source window, matching the live retention policy.
    /// </summary>
    public IReadOnlyList<MonitoringEvent> ListTimelineEvents(string jobId, int capacity)
    {
        lock (_gate)
        {
            var snapshot = ReadTimelineSnapshotLocked(jobId, capacity);
            return snapshot.HasObservedAmbiguousTimestamps
                ? snapshot.SourceWindow
                : snapshot.NewestTimestampedWindow;
        }
    }

    /// <summary>
    /// Restores both bounded candidate windows plus the historical ambiguity
    /// flag. Selecting only the currently displayed window would make the
    /// result depend on restart timing once a missing timestamp arrives.
    /// </summary>
    public LedgerTimelineSnapshot ListTimelineSnapshot(string jobId, int capacity)
    {
        lock (_gate)
        {
            return ReadTimelineSnapshotLocked(jobId, capacity);
        }
    }

    private LedgerTimelineSnapshot ReadTimelineSnapshotLocked(
        string jobId,
        int capacity)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        ValidateReadLimit(capacity);
        ThrowIfDisposed();

        var hasMissing = Convert.ToInt64(
            ExecuteScalar(
                null,
                """
                SELECT COUNT(*)
                FROM logical_events
                WHERE job_id = $job
                  AND decision = $decision
                  AND event_timestamp_utc IS NULL;
                """,
                ("$job", jobId),
                ("$decision", LedgerLogicalDecision.Applied.ToString())),
            CultureInfo.InvariantCulture) > 0;

        var sourceWindow = ReadTimelineWindow(
            jobId,
            capacity,
            newestTimestamped: false);
        var newestWindow = ReadTimelineWindow(
            jobId,
            capacity,
            newestTimestamped: true);

        return new LedgerTimelineSnapshot(
            sourceWindow,
            newestWindow,
            hasMissing);
    }

    private IReadOnlyList<MonitoringEvent> ReadTimelineWindow(
        string jobId,
        int capacity,
        bool newestTimestamped)
    {
        var sql = newestTimestamped
            ? """
              SELECT l.logical_event_id, e.event_id, e.job_id, e.event_key,
                     e.session_id, e.event_timestamp_utc, e.observed_at_utc,
                     e.category, e.severity, e.summary
              FROM logical_events l
              JOIN source_events e ON e.event_id = l.source_event_id
              WHERE l.job_id = $job
                AND l.decision = $decision
                AND e.event_timestamp_utc IS NOT NULL
              ORDER BY e.event_timestamp_utc DESC, l.logical_event_id ASC
              LIMIT $limit;
              """
            : """
              SELECT l.logical_event_id, e.event_id, e.job_id, e.event_key,
                     e.session_id, e.event_timestamp_utc, e.observed_at_utc,
                     e.category, e.severity, e.summary
              FROM logical_events l
              JOIN source_events e ON e.event_id = l.source_event_id
              WHERE l.job_id = $job AND l.decision = $decision
              ORDER BY l.logical_event_id DESC
              LIMIT $limit;
              """;

        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$decision", LedgerLogicalDecision.Applied.ToString());
        command.Parameters.AddWithValue("$limit", capacity);

        var results = new List<(long LogicalEventId, MonitoringEvent Event)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add((
                    reader.GetInt64(0),
                    new MonitoringEvent
                    {
                        EventKey = reader.GetString(3),
                        JobId = reader.GetString(2),
                        SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Timestamp = reader.IsDBNull(5)
                            ? null
                            : LedgerStoragePaths.ParseTimestamp(reader.GetString(5)),
                        ObservedAt = LedgerStoragePaths.ParseTimestamp(reader.GetString(6)),
                        Category = Enum.Parse<MonitoringEventCategory>(reader.GetString(7)),
                        Severity = Enum.Parse<MonitoringEventSeverity>(reader.GetString(8)),
                        Summary = reader.GetString(9)
                    }));
            }
        }

        return results
            .OrderBy(item => item.LogicalEventId)
            .Select(item => item.Event)
            .ToArray();
    }

    /// <summary>Bounded committed logical-event retrieval seam for B3.</summary>
    public IReadOnlyList<LedgerLogicalEvent> ListCommittedLogicalEvents(
        string jobId,
        long? afterLogicalEventId,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job id is required.", nameof(jobId));
        }

        ValidateReadLimit(limit);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT l.logical_event_id, l.source_event_id, e.job_id, e.event_key,
                       e.session_id, e.event_timestamp_utc, e.observed_at_utc,
                       e.category, e.severity, e.summary, l.cost_text, l.cost_expected
                FROM logical_events l
                JOIN source_events e ON e.event_id = l.source_event_id
                WHERE l.job_id = $job
                  AND l.decision = $decision
                  AND ($after IS NULL OR l.logical_event_id > $after)
                ORDER BY l.logical_event_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            command.Parameters.AddWithValue("$decision", LedgerLogicalDecision.Applied.ToString());
            command.Parameters.AddWithValue(
                "$after",
                afterLogicalEventId is null ? DBNull.Value : afterLogicalEventId.Value);
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<LedgerLogicalEvent>(Math.Min(limit, 256));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LedgerLogicalEvent(
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
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : ParseDecimal(reader.GetString(10)),
                    reader.GetInt64(11) != 0));
            }

            return results;
        }
    }

    private void TouchJobSource(
        SqliteTransaction transaction,
        string jobId,
        string sourcePath,
        string status,
        DateTimeOffset now)
    {
        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO job_sources(
                job_id, source_path, status, first_seen_utc, last_seen_utc)
            VALUES ($job, $path, $status, $now, $now)
            ON CONFLICT(job_id, source_path) DO UPDATE SET
                status = excluded.status,
                last_seen_utc = excluded.last_seen_utc;
            """,
            ("$job", jobId),
            ("$path", sourcePath),
            ("$status", status),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));
    }

    private MonitoringEvent? ApplyLogicalProjection(
        SqliteTransaction transaction,
        string jobId,
        long recordId,
        long sourceEventId,
        ParsedLogEvent parsed,
        DateTimeOffset now,
        ProjectionBatchStats stats)
    {
        _ = recordId;
        var identity = parsed.Identity;
        var costText = parsed.CostAmount is { } cost
            ? cost.ToString(CultureInfo.InvariantCulture)
            : null;
        var eventTimestamp = parsed.Event.Timestamp is { } timestamp
            ? LedgerStoragePaths.FormatTimestamp(timestamp)
            : null;

        if (identity is null || !identity.HasMessageAndPart)
        {
            InsertLogicalEvent(
                transaction,
                sourceEventId,
                jobId,
                LedgerLogicalDecision.MissingIdentity,
                identity,
                logicalKey: null,
                costText: null,
                costExpected: parsed.CostExpected,
                eventTimestamp,
                now);
            ExecuteNonQuery(
                transaction,
                """
                UPDATE job_aggregates
                SET missing_identity_count = missing_identity_count + 1,
                    version = version + 1,
                    updated_utc = $now
                WHERE job_id = $job;
                """,
                ("$now", LedgerStoragePaths.FormatTimestamp(now)),
                ("$job", jobId));
            stats.MissingIdentity++;
            return null;
        }

        var logicalKey = BuildLogicalKey(jobId, identity);

        var existing = ReadLogicalKey(transaction, jobId, logicalKey);
        if (existing is { } match)
        {
            if (match.CostAmount != parsed.CostAmount ||
                !string.Equals(match.EventTimestamp, eventTimestamp, StringComparison.Ordinal))
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The same logical source identity was observed with conflicting immutable cost or timestamp metadata; the batch was rolled back.");
            }

            InsertLogicalEvent(
                transaction,
                sourceEventId,
                jobId,
                LedgerLogicalDecision.Duplicate,
                identity,
                logicalKey,
                costText,
                parsed.CostExpected,
                eventTimestamp,
                now,
                applied: false);
            stats.Duplicate++;
            return null;
        }

        var logicalEventId = InsertLogicalEvent(
            transaction,
            sourceEventId,
            jobId,
            LedgerLogicalDecision.Applied,
            identity,
            logicalKey,
            costText,
            parsed.CostExpected,
            eventTimestamp,
            now);
        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO job_logical_keys(
                job_id, logical_key, logical_event_id, cost_text, event_timestamp_utc)
            VALUES ($job, $key, $event, $cost, $eventTimestamp);
            """,
            ("$job", jobId),
            ("$key", logicalKey),
            ("$event", logicalEventId),
            ("$cost", costText),
            ("$eventTimestamp", eventTimestamp));

        var costDelta = parsed.CostAmount ?? 0m;
        var currentCostText = ExecuteScalar(
            transaction,
            "SELECT cost_total FROM job_aggregates WHERE job_id = $job;",
            ("$job", jobId)) as string;
        decimal nextCost;
        try
        {
            var currentCost = string.IsNullOrWhiteSpace(currentCostText)
                ? 0m
                : ParseDecimal(currentCostText);
            nextCost = checked(currentCost + costDelta);
        }
        catch (OverflowException ex)
        {
            throw new LedgerException(
                LedgerStorageFailure.NumericOverflow,
                "The durable cost total would overflow; the batch was rolled back.",
                ex);
        }

        var missingCost = parsed.CostExpected && parsed.CostAmount is null ? 1 : 0;
        var costPresent = parsed.CostAmount is null ? 0 : 1;
        if (parsed.CostExpected && parsed.CostAmount is null)
        {
            stats.MissingCost++;
        }

        var activity = eventTimestamp ??
            LedgerStoragePaths.FormatTimestamp(now);
        ExecuteNonQuery(
            transaction,
            """
            UPDATE job_aggregates
            SET cost_total = $costTotal,
                cost_event_count = cost_event_count + $costPresent,
                missing_cost_event_count = missing_cost_event_count + $missingCost,
                latest_session_id = COALESCE($session, latest_session_id),
                last_activity_utc = CASE
                    WHEN last_activity_utc IS NULL OR $activity > last_activity_utc
                        THEN $activity
                    ELSE last_activity_utc
                END,
                logical_event_count = logical_event_count + 1,
                timeline_ambiguous = CASE
                    WHEN $ambiguous = 1 THEN 1
                    ELSE timeline_ambiguous
                END,
                version = version + 1,
                updated_utc = $now
            WHERE job_id = $job;
            """,
            ("$costTotal", nextCost.ToString(CultureInfo.InvariantCulture)),
            ("$costPresent", costPresent),
            ("$missingCost", missingCost),
            ("$session", string.IsNullOrWhiteSpace(identity.SessionId)
                ? null
                : identity.SessionId),
            ("$activity", activity),
            ("$ambiguous", eventTimestamp is null ? 1 : 0),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)),
            ("$job", jobId));

        return parsed.Event;
    }

    private long InsertLogicalEvent(
        SqliteTransaction transaction,
        long sourceEventId,
        string jobId,
        LedgerLogicalDecision decision,
        ParsedEventIdentity? identity,
        string? logicalKey,
        string? costText,
        bool costExpected,
        string? eventTimestamp,
        DateTimeOffset now,
        bool applied = true)
    {
        var inserted = ExecuteNonQuery(
            transaction,
            """
            INSERT INTO logical_events(
                source_event_id, job_id, decision, namespace, session_key,
                message_id, part_id, event_type, logical_key, cost_text,
                cost_expected, event_timestamp_utc, observed_at_utc, applied_utc)
            VALUES (
                $sourceEvent, $job, $decision, $namespace, $session,
                $message, $part, $eventType, $logicalKey, $cost,
                $costExpected, $eventTimestamp, $observedAt, $appliedUtc);
            """,
            ("$sourceEvent", sourceEventId),
            ("$job", jobId),
            ("$decision", decision.ToString()),
            ("$namespace", identity?.Namespace),
            ("$session", string.IsNullOrWhiteSpace(identity?.SessionId)
                ? null
                : identity.SessionId),
            ("$message", identity?.MessageId),
            ("$part", identity?.PartId),
            ("$eventType", identity?.CanonicalEventType),
            ("$logicalKey", logicalKey),
            ("$cost", costText),
            ("$costExpected", costExpected ? 1 : 0),
            ("$eventTimestamp", eventTimestamp),
            ("$observedAt", LedgerStoragePaths.FormatTimestamp(now)),
            ("$appliedUtc", applied ? LedgerStoragePaths.FormatTimestamp(now) : null));
        if (inserted != 1)
        {
            throw new LedgerException(
                LedgerStorageFailure.ConstraintViolation,
                "The logical event insert did not take effect; the batch was rolled back.");
        }

        return Convert.ToInt64(
            ExecuteScalar(transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);
    }

    private static string BuildLogicalKey(
        string jobId,
        ParsedEventIdentity identity)
    {
        // Length-prefixed, type-tagged components make every source value
        // unambiguous even when it contains separators or control characters.
        // The missing-session tag is distinct from a literal session value.
        var builder = new StringBuilder(LogicalKeyVersion);
        AppendLogicalComponent(builder, 'J', jobId);
        AppendLogicalComponent(builder, 'N', identity.Namespace);
        if (string.IsNullOrWhiteSpace(identity.SessionId))
        {
            builder.Append('M');
        }
        else
        {
            AppendLogicalComponent(builder, 'S', identity.SessionId);
        }

        AppendLogicalComponent(builder, 'A', identity.MessageId);
        AppendLogicalComponent(builder, 'P', identity.PartId);
        AppendLogicalComponent(builder, 'T', identity.CanonicalEventType);
        return builder.ToString();
    }

    private static void AppendLogicalComponent(
        StringBuilder builder,
        char kind,
        string? value)
    {
        builder.Append(kind);
        if (value is null)
        {
            builder.Append('-');
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private (decimal? CostAmount, string? EventTimestamp)? ReadLogicalKey(
        SqliteTransaction transaction,
        string jobId,
        string logicalKey)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT cost_text, event_timestamp_utc
            FROM job_logical_keys
            WHERE job_id = $job AND logical_key = $key;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$key", logicalKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var costText = reader.IsDBNull(0) ? null : reader.GetString(0);
        return (
            costText is null ? null : ParseDecimal(costText),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private ProjectionBatchOutcome? FinalizeProjectionBatch(
        SqliteTransaction transaction,
        string jobId,
        long generationId,
        long committedOffset,
        ProjectionBatchStats stats,
        bool partialTrailing,
        DateTimeOffset now)
    {
        if (!_schema.SupportsProjection)
        {
            return null;
        }

        UpdateBootstrapCheckpoint(transaction, generationId, committedOffset, now);
        ApplyCoverageBatch(
            transaction,
            jobId,
            generationId,
            committedOffset,
            stats,
            partialTrailing,
            now);
        RefreshJobCoverage(transaction, jobId, now);
        return new ProjectionBatchOutcome(
            ReadAggregate(transaction, jobId),
            ReadBootstrap(transaction, generationId),
            ReadCoverage(transaction, jobId));
    }

    private void UpdateBootstrapCheckpoint(
        SqliteTransaction transaction,
        long generationId,
        long committedOffset,
        DateTimeOffset now)
    {
        ExecuteNonQuery(
            transaction,
            """
            UPDATE bootstrap_sources
            SET committed_offset = $cursor,
                status = CASE
                    WHEN $cursor >= initial_boundary THEN $clean
                    ELSE $bootstrapping
                END,
                completed_utc = CASE
                    WHEN $cursor >= initial_boundary THEN COALESCE(completed_utc, $now)
                    ELSE completed_utc
                END,
                updated_utc = $now
            WHERE generation_id = $generation;
            """,
            ("$cursor", committedOffset),
            ("$clean", LedgerCoverageState.Clean.ToString()),
            ("$bootstrapping", LedgerCoverageState.Bootstrapping.ToString()),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)),
            ("$generation", generationId));
    }

    private void ApplyCoverageBatch(
        SqliteTransaction transaction,
        string jobId,
        long generationId,
        long committedOffset,
        ProjectionBatchStats stats,
        bool partialTrailing,
        DateTimeOffset now)
    {
        _ = jobId;
        var status = partialTrailing
            ? LedgerCoverageState.Partial
            : stats.MissingIdentity > 0
                ? LedgerCoverageState.MissingIdentity
                : stats.MissingCost > 0
                    ? LedgerCoverageState.MissingCost
                    : stats.Malformed + stats.Invalid + stats.Overlong > 0
                        ? LedgerCoverageState.Malformed
                        : LedgerCoverageState.Clean;

        ExecuteNonQuery(
            transaction,
            """
            UPDATE source_coverage
            SET status = $status,
                clean_until_offset = CASE
                    WHEN $partial = 0 THEN $cursor
                    ELSE clean_until_offset
                END,
                partial_trailing = $partial,
                malformed_count = malformed_count + $malformed,
                invalid_count = invalid_count + $invalid,
                overlong_count = overlong_count + $overlong,
                missing_identity_count = missing_identity_count + $missingIdentity,
                missing_cost_count = missing_cost_count + $missingCost,
                duplicate_count = duplicate_count + $duplicates,
                updated_utc = $now
            WHERE generation_id = $generation;
            """,
            ("$status", status.ToString()),
            ("$partial", partialTrailing ? 1 : 0),
            ("$cursor", committedOffset),
            ("$malformed", stats.Malformed),
            ("$invalid", stats.Invalid),
            ("$overlong", stats.Overlong),
            ("$missingIdentity", stats.MissingIdentity),
            ("$missingCost", stats.MissingCost),
            ("$duplicates", stats.Duplicate),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)),
            ("$generation", generationId));
    }

    private void RefreshJobCoverage(
        SqliteTransaction transaction,
        string jobId,
        DateTimeOffset now)
    {
        var state = LedgerCoverageState.Clean;
        var detail = "Historical coverage is clean through the committed boundary.";
        var jobConflict = ExecuteScalar(
            transaction,
            "SELECT conflict_detail FROM jobs WHERE job_id = $job;",
            ("$job", jobId)) as string;
        var provisional = Convert.ToInt64(
            ExecuteScalar(
                transaction,
                "SELECT provisional FROM jobs WHERE job_id = $job;",
                ("$job", jobId)) ?? 0L,
            CultureInfo.InvariantCulture) != 0;
        var uncoveredDetail = ExecuteScalar(
            transaction,
            "SELECT uncovered_detail FROM jobs WHERE job_id = $job;",
            ("$job", jobId)) as string;

        var sourceStatuses = new List<string>();
        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT status FROM job_sources WHERE job_id = $job;";
            command.Parameters.AddWithValue("$job", jobId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sourceStatuses.Add(reader.GetString(0));
            }
        }

        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT
                    COALESCE(SUM(partial_trailing), 0),
                    COALESCE(SUM(malformed_count), 0),
                    COALESCE(SUM(invalid_count), 0),
                    COALESCE(SUM(overlong_count), 0),
                    COALESCE(SUM(missing_identity_count), 0),
                    COALESCE(SUM(missing_cost_count), 0),
                    COALESCE(SUM(conflict_count), 0),
                    COALESCE(SUM(CASE WHEN COALESCE(b.completed_utc, '') = '' THEN 1 ELSE 0 END), 0)
                FROM source_coverage c
                LEFT JOIN bootstrap_sources b ON b.generation_id = c.generation_id
                WHERE c.job_id = $job;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var partial = reader.GetInt64(0);
                var malformed = reader.GetInt64(1);
                var invalid = reader.GetInt64(2);
                var overlong = reader.GetInt64(3);
                var missingIdentity = reader.GetInt64(4);
                var missingCost = reader.GetInt64(5);
                var conflict = reader.GetInt64(6);
                var bootstrapping = reader.GetInt64(7);

                if (!string.IsNullOrWhiteSpace(jobConflict) ||
                    sourceStatuses.Any(status =>
                        string.Equals(status, LedgerCoverageState.Conflict.ToString(), StringComparison.Ordinal)) ||
                    conflict > 0)
                {
                    state = LedgerCoverageState.Conflict;
                    detail = jobConflict ??
                        "Identity conflicts or conflicting source associations were observed.";
                }
                else if (provisional)
                {
                    state = LedgerCoverageState.Provisional;
                    detail = "Job identity or metadata is provisional; observed coverage is incomplete.";
                }
                else if (!string.IsNullOrWhiteSpace(uncoveredDetail))
                {
                    state = LedgerCoverageState.Uncovered;
                    detail = uncoveredDetail;
                }
                else if (sourceStatuses.Any(status =>
                             string.Equals(status, LedgerCoverageState.Unavailable.ToString(), StringComparison.Ordinal)))
                {
                    state = LedgerCoverageState.Unavailable;
                    detail = "One or more associated sources are currently unavailable; committed data is retained.";
                }
                else if (sourceStatuses.Any(status =>
                             string.Equals(status, LedgerCoverageState.Malformed.ToString(), StringComparison.Ordinal)))
                {
                    state = LedgerCoverageState.Malformed;
                    detail = "One or more associated sources contain a record outside the bounded ingestion window.";
                }
                else if (sourceStatuses.Any(status =>
                             string.Equals(status, LedgerCoverageState.Provisional.ToString(), StringComparison.Ordinal)))
                {
                    state = LedgerCoverageState.Provisional;
                    detail = "One or more associated sources have provisional identity or metadata.";
                }
                else if (partial > 0)
                {
                    state = LedgerCoverageState.Partial;
                    detail = "A partial trailing record is waiting for completion; it has not been counted.";
                }
                else if (missingIdentity > 0)
                {
                    state = LedgerCoverageState.MissingIdentity;
                    detail = "Some records have no message/part identity and were recorded without aggregate effects.";
                }
                else if (missingCost > 0)
                {
                    state = LedgerCoverageState.MissingCost;
                    detail = "Some step-finish records did not report a usable cost; the observed total is incomplete.";
                }
                else if (malformed + invalid + overlong > 0)
                {
                    state = LedgerCoverageState.Malformed;
                    detail = "Malformed, invalid, or overlong records were recorded without event aggregates.";
                }
                else if (bootstrapping > 0)
                {
                    state = LedgerCoverageState.Bootstrapping;
                    detail = "Historical bootstrap is still in progress; reported totals are committed subtotals.";
                }
            }
        }

        ExecuteNonQuery(
            transaction,
            """
            INSERT INTO job_coverage(job_id, status, detail, updated_utc)
            VALUES ($job, $status, $detail, $now)
            ON CONFLICT(job_id) DO UPDATE SET
                status = excluded.status,
                detail = excluded.detail,
                updated_utc = excluded.updated_utc;
            """,
            ("$job", jobId),
            ("$status", state.ToString()),
            ("$detail", detail),
            ("$now", LedgerStoragePaths.FormatTimestamp(now)));
    }

    private LedgerJobAggregate ReadAggregate(
        SqliteTransaction? transaction,
        string jobId)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT job_id, cost_total, cost_event_count, missing_cost_event_count,
                   latest_session_id, last_activity_utc, logical_event_count,
                   missing_identity_count, timeline_ambiguous, version
            FROM job_aggregates
            WHERE job_id = $job;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return LedgerJobAggregate.Empty(jobId);
        }

        return new LedgerJobAggregate(
            reader.GetString(0),
            ParseDecimal(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5)
                ? null
                : LedgerStoragePaths.ParseTimestamp(reader.GetString(5)),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8) != 0,
            reader.GetInt64(9));
    }

    private LedgerBootstrapProgress ReadBootstrap(
        SqliteTransaction transaction,
        long generationId)
    {
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT generation_id, initial_boundary, committed_offset,
                   completed_utc, status
            FROM bootstrap_sources
            WHERE generation_id = $generation;
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new LedgerBootstrapProgress(generationId, 0, 0, null, "Unknown");
        }

        return new LedgerBootstrapProgress(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3)
                ? null
                : LedgerStoragePaths.ParseTimestamp(reader.GetString(3)),
            reader.GetString(4));
    }

    private LedgerCoverage ReadCoverage(
        SqliteTransaction? transaction,
        string jobId)
    {
        var state = LedgerCoverageState.Clean;
        var detail = "Historical coverage is clean through the committed boundary.";
        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT status, detail FROM job_coverage WHERE job_id = $job;";
            command.Parameters.AddWithValue("$job", jobId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (Enum.TryParse<LedgerCoverageState>(reader.GetString(0), out var parsed))
                {
                    state = parsed;
                }

                detail = reader.IsDBNull(1) ? detail : reader.GetString(1);
            }
        }

        long malformed = 0;
        long invalid = 0;
        long overlong = 0;
        long missingIdentity = 0;
        long missingCost = 0;
        long partial = 0;
        long conflicts = 0;
        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT
                    COALESCE(SUM(malformed_count), 0),
                    COALESCE(SUM(invalid_count), 0),
                    COALESCE(SUM(overlong_count), 0),
                    COALESCE(SUM(missing_identity_count), 0),
                    COALESCE(SUM(missing_cost_count), 0),
                    COALESCE(SUM(partial_trailing), 0),
                    COALESCE(SUM(conflict_count), 0)
                FROM source_coverage
                WHERE job_id = $job;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                malformed = reader.GetInt64(0);
                invalid = reader.GetInt64(1);
                overlong = reader.GetInt64(2);
                missingIdentity = reader.GetInt64(3);
                missingCost = reader.GetInt64(4);
                partial = reader.GetInt64(5);
                conflicts = reader.GetInt64(6);
            }
        }

        long unavailable = 0;
        using (var command = Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM job_sources
                WHERE job_id = $job AND status = $status;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            command.Parameters.AddWithValue("$status", LedgerCoverageState.Unavailable.ToString());
            unavailable = Convert.ToInt64(
                command.ExecuteScalar() ?? 0L,
                CultureInfo.InvariantCulture);
        }

        return new LedgerCoverage(
            jobId,
            state,
            detail,
            malformed,
            invalid,
            overlong,
            missingIdentity,
            missingCost,
            partial,
            unavailable,
            conflicts);
    }

    private static decimal ParseDecimal(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private sealed class ProjectionBatchStats
    {
        public int Malformed { get; set; }
        public int Invalid { get; set; }
        public int Overlong { get; set; }
        public int MissingIdentity { get; set; }
        public int MissingCost { get; set; }
        public int Duplicate { get; set; }
        public int PartialTrailing { get; set; }

        public void AddDisposition(LedgerRecordDisposition disposition)
        {
            switch (disposition)
            {
                case LedgerRecordDisposition.MalformedJson:
                    Malformed++;
                    break;
                case LedgerRecordDisposition.InvalidUtf8:
                    Invalid++;
                    break;
                case LedgerRecordDisposition.Overlong:
                    Overlong++;
                    break;
            }
        }
    }

    private sealed record ProjectionBatchOutcome(
        LedgerJobAggregate Aggregate,
        LedgerBootstrapProgress Bootstrap,
        LedgerCoverage Coverage);

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
        DateTimeOffset now,
        long initialBoundary)
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

        if (_schema.SupportsProjection)
        {
            // The boundary is captured once, before the first batch. It is a
            // resumable bootstrap target, not a limit on later live appends.
            var bootstrapInserted = ExecuteNonQuery(
                transaction,
                """
                INSERT INTO bootstrap_sources(
                    generation_id, job_id, initial_boundary, committed_offset,
                    completed_utc, status, updated_utc)
                VALUES ($generation, $job, $boundary, 0, NULL, $status, $now);
                """,
                ("$generation", generationId),
                ("$job", jobId),
                ("$boundary", initialBoundary),
                ("$status", initialBoundary == 0 ? "Clean" : "Bootstrapping"),
                ("$now", LedgerStoragePaths.FormatTimestamp(now)));
            if (bootstrapInserted != 1)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The bootstrap checkpoint insert did not take effect; the batch was rolled back.");
            }

            if (initialBoundary == 0)
            {
                ExecuteNonQuery(
                    transaction,
                    """
                    UPDATE bootstrap_sources
                    SET completed_utc = $now
                    WHERE generation_id = $generation;
                    """,
                    ("$now", LedgerStoragePaths.FormatTimestamp(now)),
                    ("$generation", generationId));
            }

            var coverageInserted = ExecuteNonQuery(
                transaction,
                """
                INSERT INTO source_coverage(
                    generation_id, job_id, status, clean_until_offset,
                    partial_trailing, malformed_count, invalid_count, overlong_count,
                    missing_identity_count, missing_cost_count, duplicate_count,
                    conflict_count, updated_utc)
                VALUES (
                    $generation, $job, $status, 0,
                    0, 0, 0, 0,
                    0, 0, 0,
                    0, $now);
                """,
                ("$generation", generationId),
                ("$job", jobId),
                ("$status", initialBoundary == 0
                    ? LedgerCoverageState.Clean.ToString()
                    : LedgerCoverageState.Bootstrapping.ToString()),
                ("$now", LedgerStoragePaths.FormatTimestamp(now)));
            if (coverageInserted != 1)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The source coverage insert did not take effect; the batch was rolled back.");
            }
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
        int expectedEvents,
        long? expectedProjectionVersion = null,
        int? expectedLogicalEvents = null)
    {
        _options.BeforeCommitFaultInjector?.Invoke();
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
            expectedEvents,
            expectedProjectionVersion,
            expectedLogicalEvents);
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
        int expectedEvents,
        long? expectedProjectionVersion = null,
        int? expectedLogicalEvents = null)
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

            if (expectedLogicalEvents is { } expectedLogical)
            {
                var logicalCount = Convert.ToInt64(
                    ExecuteScalar(
                        connection,
                        transaction,
                        """
                        SELECT COUNT(*)
                        FROM logical_events l
                        JOIN source_events e ON e.event_id = l.source_event_id
                        JOIN source_records r ON r.record_id = e.record_id
                        WHERE r.generation_id = $generation
                          AND r.start_byte >= $start
                          AND r.start_byte < $end;
                        """,
                        ("$generation", generationId),
                        ("$start", start),
                        ("$end", end)),
                    CultureInfo.InvariantCulture);
                if (logicalCount != expectedLogical)
                {
                    throw new LedgerException(
                        LedgerStorageFailure.ConstraintViolation,
                        "The logical projection postcondition failed; the batch was rolled back.");
                }
            }
        }

        if (expectedProjectionVersion is { } expectedVersion)
        {
            var aggregateVersion = ExecuteScalar(
                connection,
                transaction,
                "SELECT version FROM job_aggregates WHERE job_id = $job;",
                ("$job", jobId));
            if (aggregateVersion is null or DBNull ||
                Convert.ToInt64(aggregateVersion, CultureInfo.InvariantCulture) != expectedVersion)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The aggregate postcondition failed; the batch was rolled back.");
            }

            var checkpointOffset = ExecuteScalar(
                connection,
                transaction,
                "SELECT committed_offset FROM bootstrap_sources WHERE generation_id = $generation;",
                ("$generation", generationId));
            if (checkpointOffset is null or DBNull ||
                Convert.ToInt64(checkpointOffset, CultureInfo.InvariantCulture) != expectedCursor)
            {
                throw new LedgerException(
                    LedgerStorageFailure.ConstraintViolation,
                    "The bootstrap checkpoint postcondition failed; the batch was rolled back.");
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

    private long InsertEvent(
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

        return Convert.ToInt64(
            ExecuteScalar(transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);
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
