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
    public const int CurrentVersion = 1;
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
    RecordTooLarge
}

public enum LedgerStorageFailure
{
    Unavailable,
    Locked,
    Corrupt,
    IncompatibleSchema,
    ConstraintViolation,
    StaleCursor
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
}

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

    public static string DefaultDatabasePath =>
        Path.Combine(DefaultDataDirectory, "events.db");

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
