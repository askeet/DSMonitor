using System.Globalization;
using System.IO;
using DSMonitor.Models;

namespace DSMonitor.Persistence;

/// <summary>
/// Schema version owned by the durable event ledger. Opening a database with
/// any other version fails closed; the ledger never migrates, deletes, or
/// reinitializes existing data implicitly.
/// </summary>
public static class EventLedgerSchema
{
    /// <summary>B1 physical-only ledger schema.</summary>
    public const int CurrentVersion = 1;

    /// <summary>B2 projection schema in the separate fresh database.</summary>
    public const int ProjectionVersion = 2;
}

/// <summary>
/// Physical source identity used for generation continuity. The identity is
/// taken from the opened handle, never from a path string. A weak/zero
/// identity is rejected because it cannot distinguish a replacement.
/// </summary>
public readonly record struct LedgerSourceIdentity(
    uint VolumeSerial,
    ulong FileIndex,
    long CreationTimeUtcTicks)
{
    public bool IsVerified =>
        VolumeSerial != 0 &&
        FileIndex != 0 &&
        CreationTimeUtcTicks != 0;
}

public enum LedgerRecordDisposition
{
    Accepted,
    Empty,
    MalformedJson,
    InvalidUtf8,
    Overlong
}

public enum LedgerIngestStatus
{
    Committed,
    NoData,
    SourceUnavailable,
    AmbiguousSourceIdentity,
    ContinuityUncertain,
    RecordTooLarge,
    IdentityConflict
}

public enum LedgerLogicalDecision
{
    Applied,
    Duplicate,
    MissingIdentity
}

public enum LedgerCoverageState
{
    Clean,
    Bootstrapping,
    Partial,
    Malformed,
    MissingIdentity,
    MissingCost,
    Unavailable,
    Conflict,
    Provisional,
    Uncovered,
    Incomplete
}

public enum LedgerStorageFailure
{
    Unavailable,
    Locked,
    Corrupt,
    IncompatibleSchema,
    ConstraintViolation,
    StaleCursor,
    NumericOverflow
}

public sealed class LedgerException : Exception
{
    public LedgerException(
        LedgerStorageFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public LedgerStorageFailure Failure { get; }
}

public sealed record LedgerIngestResult(
    LedgerIngestStatus Status,
    long? GenerationId,
    long CommittedOffset,
    int RecordsCommitted,
    int EventsCommitted,
    IReadOnlyList<string> Warnings)
{
    public bool IsSuccess => Status is LedgerIngestStatus.Committed or LedgerIngestStatus.NoData;

    /// <summary>
    /// Newly applied logical events in source order. Physical duplicates and
    /// missing-identity records are deliberately not included because they
    /// must not affect the timeline, aggregates, or external logging.
    /// </summary>
    public IReadOnlyList<MonitoringEvent> AcceptedEvents { get; init; } =
        Array.Empty<MonitoringEvent>();

    /// <summary>Committed aggregate snapshot after the batch, for B2 callers.</summary>
    public LedgerJobAggregate? Aggregate { get; init; }

    public LedgerBootstrapProgress? Bootstrap { get; init; }

    public LedgerCoverage? Coverage { get; init; }

    public bool PartialTrailingRecord { get; init; }
}

/// <summary>
/// Durable aggregate owned by the ledger. Cost is a decimal string in SQLite
/// so binary floating-point rounding cannot change the committed total.
/// </summary>
public sealed record LedgerJobAggregate(
    string JobId,
    decimal CostTotal,
    long CostEventCount,
    long MissingCostEventCount,
    string? LatestSessionId,
    DateTimeOffset? LastActivity,
    long LogicalEventCount,
    long MissingIdentityCount,
    bool TimelineAmbiguous,
    long Version)
{
    public bool HasCost => CostEventCount > 0;

    public bool IsCostComplete => CostEventCount > 0 && MissingCostEventCount == 0;

    public static LedgerJobAggregate Empty(string jobId) =>
        new(jobId, 0m, 0, 0, null, null, 0, 0, false, 0);
}

public sealed record LedgerBootstrapProgress(
    long GenerationId,
    long InitialBoundary,
    long CommittedOffset,
    DateTimeOffset? CompletedAt,
    string Status)
{
    public bool IsComplete => CompletedAt is not null || CommittedOffset >= InitialBoundary;

    public bool IsWithinInitialBoundary => CommittedOffset <= InitialBoundary;
}

public sealed record LedgerCoverage(
    string JobId,
    LedgerCoverageState State,
    string Detail,
    long MalformedRecords,
    long InvalidRecords,
    long OverlongRecords,
    long MissingIdentityRecords,
    long MissingCostRecords,
    long PartialTrailingSources,
    long UnavailableSources,
    long ConflictSources);

public sealed record LedgerJobSource(
    string JobId,
    string SourcePath,
    string Status,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record LedgerDiscoveryState(
    string RunsRoot,
    bool DirectoryAvailable,
    IReadOnlyList<string> Warnings,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Restorable bounded timeline state. Both candidate windows are required so a
/// restart is partition-invariant: the source window remains the recent source
/// events and the newest window remains the newest timestamped events, even
/// when only one of them is currently displayed.
/// </summary>
public sealed record LedgerTimelineSnapshot(
    IReadOnlyList<MonitoringEvent> SourceWindow,
    IReadOnlyList<MonitoringEvent> NewestTimestampedWindow,
    bool HasObservedAmbiguousTimestamps);

/// <summary>Bounded committed logical-event retrieval seam for B3.</summary>
public sealed record LedgerLogicalEvent(
    long LogicalEventId,
    long SourceEventId,
    string JobId,
    string EventKey,
    string? SessionId,
    DateTimeOffset? EventTimestamp,
    DateTimeOffset ObservedAt,
    MonitoringEventCategory Category,
    MonitoringEventSeverity Severity,
    string Summary,
    decimal? Cost,
    bool CostExpected);

public sealed record LedgerGeneration(
    long GenerationId,
    string JobId,
    string SourcePath,
    LedgerSourceIdentity Identity,
    long CommittedOffset,
    long CursorVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long? PredecessorGenerationId);

public sealed record LedgerRecord(
    long RecordId,
    long GenerationId,
    long StartByte,
    long EndByte,
    long PayloadLength,
    LedgerRecordDisposition Disposition,
    string ContentSha256);

public sealed record LedgerEventMetadata(
    long EventId,
    long RecordId,
    string JobId,
    string EventKey,
    string? SessionId,
    DateTimeOffset? EventTimestamp,
    DateTimeOffset ObservedAt,
    MonitoringEventCategory Category,
    MonitoringEventSeverity Severity,
    string Summary);

/// <summary>
/// Bounded ledger limits. No retry loop is performed by the ledger; SQLite's
/// busy timeout is the only wait and is itself bounded.
/// </summary>
public sealed class EventLedgerOptions
{
    public int BusyTimeoutMilliseconds { get; init; } = 5_000;

    public int MaxBytesPerTransaction { get; init; } = 512 * 1024;

    public int MaxLineBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Test-only deterministic failure injection. Production callers never set
    /// this; when present it runs immediately before the commit postconditions
    /// so the complete batch must roll back.
    /// </summary>
    internal Action? BeforeCommitFaultInjector { get; init; }

    internal int EffectiveBusyTimeout =>
        Math.Clamp(BusyTimeoutMilliseconds, 0, 30_000);

    internal int EffectiveMaxBytes =>
        Math.Clamp(MaxBytesPerTransaction, 64, 4 * 1024 * 1024);

    internal int EffectiveMaxLine =>
        Math.Clamp(MaxLineBytes, 1, EffectiveMaxBytes);
}

public delegate LedgerSourceIdentity LedgerSourceIdentityReader(FileStream stream);

public static class LedgerStoragePaths
{
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSMonitor",
        "Data");

    /// <summary>B1 physical ledger path. B2 never opens, migrates, or deletes it.</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(DefaultDataDirectory, "events.db");

    /// <summary>Fixed B2 fresh projection database path.</summary>
    public static string DefaultProjectionDatabasePath =>
        Path.Combine(DefaultDataDirectory, "events-v2.db");

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
