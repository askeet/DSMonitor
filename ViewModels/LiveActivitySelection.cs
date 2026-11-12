using DSMonitor.Models;

namespace DSMonitor.ViewModels;

/// <summary>
/// Stable job-scoped activity identity used to preserve a selected history row
/// across refreshes. A newer physical record for the same exact part keeps the
/// selection; job, source path, session, message and part must all match.
/// </summary>
internal readonly record struct LiveActivitySelectionIdentity(
    string JobId,
    string SourcePath,
    string SessionId,
    string MessageId,
    string PartId)
{
    public static LiveActivitySelectionIdentity FromEntry(LiveActivityHistoryEntry entry) =>
        new(
            entry.JobId,
            entry.SourcePath,
            entry.SessionId,
            entry.MessageId,
            entry.PartId);
}

/// <summary>
/// Exact source-record revision used to decide whether the selected activity's
/// complete text must be retrieved again. A same-part update at a new offset or
/// with a new digest produces a new key; unrelated refreshes do not.
/// </summary>
internal readonly record struct LiveActivityLoadKey(
    string JobId,
    string SourcePath,
    string SessionId,
    string MessageId,
    string PartId,
    long ByteOffset,
    string RecordDigest)
{
    public static LiveActivityLoadKey FromEntry(LiveActivityHistoryEntry entry) =>
        new(
            entry.JobId,
            entry.SourcePath,
            entry.SessionId,
            entry.MessageId,
            entry.PartId,
            entry.ByteOffset,
            entry.RecordDigest);

    public static LiveActivityLoadKey FromRow(LiveActivityHistoryRowViewModel row) =>
        FromEntry(row.Entry);
}
