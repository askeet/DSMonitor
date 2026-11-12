using DSMonitor.Models;
using DSMonitor.Persistence;

namespace DSMonitor.Services;

/// <summary>
/// Shared display/retention ordering for the transient Live Activity history.
/// Known source timestamps come before unknown timestamps; both groups are
/// newest first. Byte offset is the deterministic tie-break, with exact
/// ordinal identity fields making equal-offset order stable.
/// </summary>
internal static class LiveActivityHistoryPolicy
{
    public static IReadOnlyList<LiveActivityHistoryEntry> Order(
        IEnumerable<LiveActivityHistoryEntry> entries) =>
        entries
            .OrderBy(entry => entry.SourceTimestamp is null)
            .ThenByDescending(entry => entry.SourceTimestamp ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.ByteOffset)
            .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
            .ThenBy(entry => entry.MessageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.PartId, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Bounded, transient per-job accumulation of job-specific message/part
/// activity. It never reads a source, writes state, or persists content. A
/// refresh may extend the previous history only when the current guarded read
/// proves the same exact job, verified path, physical identity, generation,
/// nondecreasing captured length, and every previously retained record that is
/// still inside the new bounded window reappears at the same byte offset with
/// the same identity and record digest (and at least one such overlap exists).
/// Otherwise the prior history is discarded and replaced by the current
/// successful read.
///
/// One entry is retained per exact ordinal (session, message, part) identity
/// inside the job/generation. The newest physical record wins; an older reread
/// never overwrites a newer record. Entries and text are bounded.
/// </summary>
internal sealed class LiveActivityHistoryBuffer
{
    private Dictionary<(string Session, string Message, string PartId), LiveActivityHistoryEntry>
        _entries = new();

    public string? JobId { get; private set; }
    public string? VerifiedPath { get; private set; }
    public LedgerSourceIdentity PhysicalIdentity { get; private set; }
    public long GenerationId { get; private set; }
    public long CapturedLength { get; private set; }

    public IReadOnlyList<LiveActivityHistoryEntry> Entries =>
        _entries.Count == 0
            ? Array.Empty<LiveActivityHistoryEntry>()
            : LiveActivityHistoryPolicy.Order(_entries.Values);

    public void Clear()
    {
        _entries = new();
        JobId = null;
        VerifiedPath = null;
        PhysicalIdentity = default;
        GenerationId = 0;
        CapturedLength = 0;
    }

    /// <summary>
    /// Merge one successful bounded read into the job's history. Returns the
    /// resulting ordered, bounded snapshot. An unsuccessful/absent read window
    /// clears the buffer; the caller normally avoids calling this on failure.
    /// </summary>
    public IReadOnlyList<LiveActivityHistoryEntry> Apply(
        LiveActivitySnapshot read,
        string jobId,
        string verifiedPath,
        LedgerSourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(observation);

        if (!read.HasReadWindow)
        {
            Clear();
            return Array.Empty<LiveActivityHistoryEntry>();
        }

        var current = BuildCurrent(read.History, jobId);
        if (!observation.IsConsistent ||
            string.IsNullOrWhiteSpace(jobId) ||
            string.IsNullOrWhiteSpace(verifiedPath) ||
            !string.Equals(observation.JobId, jobId, StringComparison.Ordinal))
        {
            Clear();
            return Array.Empty<LiveActivityHistoryEntry>();
        }

        if (!CanCarry(read, jobId, verifiedPath, observation, current))
        {
            Seed(jobId, verifiedPath, observation, current);
            return Entries;
        }

        // An append-only source must never rewrite a previously complete
        // record in place. If it did, mixing old and new rows would be unsafe;
        // discard the unproven history and seed only the current read.
        foreach (var entry in current.Values)
        {
            if (_entries.TryGetValue(Identity(entry), out var prior) &&
                prior.ByteOffset == entry.ByteOffset &&
                !DigestEquals(prior.RecordDigest, entry.RecordDigest))
            {
                Seed(jobId, verifiedPath, observation, current);
                return Entries;
            }
        }

        var merged = new Dictionary<(string Session, string Message, string PartId),
            LiveActivityHistoryEntry>(_entries);
        foreach (var entry in current.Values)
        {
            var key = Identity(entry);
            if (!merged.TryGetValue(key, out var existing) ||
                entry.ByteOffset > existing.ByteOffset)
            {
                // Newest physical record wins. Equal offsets with an equal
                // digest keep the first observation time; lower offsets never
                // overwrite a newer record.
                merged[key] = entry;
            }
        }

        _entries = merged;
        UpdateEvidence(jobId, verifiedPath, observation);
        Trim();
        return Entries;
    }

    private bool CanCarry(
        LiveActivitySnapshot read,
        string jobId,
        string verifiedPath,
        LedgerSourceObservation observation,
        IReadOnlyDictionary<(string Session, string Message, string PartId),
            LiveActivityHistoryEntry> current)
    {
        if (_entries.Count == 0 ||
            JobId is null ||
            VerifiedPath is null)
        {
            return false;
        }

        if (!string.Equals(JobId, jobId, StringComparison.Ordinal) ||
            !string.Equals(VerifiedPath, verifiedPath, StringComparison.Ordinal) ||
            !PhysicalIdentity.IsVerified ||
            !PhysicalIdentity.Equals(observation.PhysicalIdentity) ||
            GenerationId != observation.GenerationId ||
            observation.CapturedLength < CapturedLength)
        {
            return false;
        }

        // The reader exposes every complete record it parsed, before identity
        // de-duplication. Every previously retained record that falls inside
        // the new window must therefore still exist at the same absolute offset
        // with the same exact identity and record digest. A single match is not
        // enough: an unchanging later record must not hide a rewrite earlier in
        // the same already-read overlap.
        if (read.HistoryRecords.Count > 0)
        {
            var observedByOffset = BuildObservedByOffset(read.HistoryRecords, jobId);
            if (observedByOffset is null)
            {
                return false;
            }

            var sawOverlap = false;
            foreach (var prior in _entries.Values)
            {
                if (prior.ByteOffset < read.ReadWindowStart ||
                    prior.ByteOffset >= read.ReadWindowEnd)
                {
                    continue;
                }

                if (!observedByOffset.TryGetValue(prior.ByteOffset, out var observed) ||
                    !SameRecord(prior, observed))
                {
                    return false;
                }

                sawOverlap = true;
            }

            return sawOverlap;
        }

        // Snapshots constructed without pre-dedup record evidence (for example
        // by a test or another caller) retain the older single-match proof.
        // Production reads always populate HistoryRecords. Even here, a prior
        // offset whose surviving record no longer matches cannot be carried.
        var currentByOffset = new Dictionary<long, LiveActivityHistoryEntry>();
        foreach (var entry in current.Values)
        {
            if (currentByOffset.TryGetValue(entry.ByteOffset, out var existing) &&
                !SameRecord(existing, entry))
            {
                return false;
            }

            currentByOffset[entry.ByteOffset] = entry;
        }

        foreach (var prior in _entries.Values)
        {
            if (prior.ByteOffset < read.ReadWindowStart ||
                prior.ByteOffset >= read.ReadWindowEnd)
            {
                continue;
            }

            if (currentByOffset.TryGetValue(prior.ByteOffset, out var observedAtOffset) &&
                !SameRecord(prior, observedAtOffset))
            {
                return false;
            }
        }

        foreach (var prior in _entries.Values)
        {
            if (prior.ByteOffset < read.ReadWindowStart ||
                prior.ByteOffset >= read.ReadWindowEnd)
            {
                continue;
            }

            if (!current.TryGetValue(Identity(prior), out var observed) ||
                observed.ByteOffset != prior.ByteOffset ||
                !DigestEquals(observed.RecordDigest, prior.RecordDigest))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static Dictionary<long, LiveActivityHistoryEntry>? BuildObservedByOffset(
        IEnumerable<LiveActivityHistoryEntry> records,
        string jobId)
    {
        var observed = new Dictionary<long, LiveActivityHistoryEntry>();
        foreach (var record in records)
        {
            if (!string.Equals(record.JobId, jobId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(record.SessionId) ||
                string.IsNullOrWhiteSpace(record.MessageId) ||
                string.IsNullOrWhiteSpace(record.PartId) ||
                string.IsNullOrWhiteSpace(record.RecordDigest))
            {
                continue;
            }

            if (observed.TryGetValue(record.ByteOffset, out var existing) &&
                !SameRecord(existing, record))
            {
                // Two different records claiming one byte offset cannot prove
                // continuity; fail closed rather than choosing one.
                return null;
            }

            observed[record.ByteOffset] = record;
        }

        return observed;
    }

    private static bool SameRecord(
        LiveActivityHistoryEntry left,
        LiveActivityHistoryEntry right) =>
        left.ByteOffset == right.ByteOffset &&
        Identity(left) == Identity(right) &&
        DigestEquals(left.RecordDigest, right.RecordDigest);

    private void Seed(
        string jobId,
        string verifiedPath,
        LedgerSourceObservation observation,
        IReadOnlyDictionary<(string Session, string Message, string PartId),
            LiveActivityHistoryEntry> current)
    {
        _entries = new Dictionary<(string Session, string Message, string PartId),
            LiveActivityHistoryEntry>(current);
        UpdateEvidence(jobId, verifiedPath, observation);
        Trim();
    }

    private void UpdateEvidence(
        string jobId,
        string verifiedPath,
        LedgerSourceObservation observation)
    {
        JobId = jobId;
        VerifiedPath = verifiedPath;
        PhysicalIdentity = observation.PhysicalIdentity;
        GenerationId = observation.GenerationId;
        CapturedLength = observation.CapturedLength;
    }

    private void Trim()
    {
        if (_entries.Count <= JobLiveActivityReader.MaxHistoryEntries)
        {
            return;
        }

        var retained = LiveActivityHistoryPolicy
            .Order(_entries.Values)
            .Take(JobLiveActivityReader.MaxHistoryEntries);
        _entries = retained.ToDictionary(
            Identity,
            entry => entry);
    }

    private static Dictionary<(string Session, string Message, string PartId),
        LiveActivityHistoryEntry> BuildCurrent(
        IEnumerable<LiveActivityHistoryEntry> history,
        string jobId)
    {
        var current = new Dictionary<(string Session, string Message, string PartId),
            LiveActivityHistoryEntry>();
        foreach (var entry in history)
        {
            if (!string.Equals(entry.JobId, jobId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.SessionId) ||
                string.IsNullOrWhiteSpace(entry.MessageId) ||
                string.IsNullOrWhiteSpace(entry.PartId) ||
                string.IsNullOrWhiteSpace(entry.RecordDigest))
            {
                continue;
            }

            var key = Identity(entry);
            if (!current.TryGetValue(key, out var existing) ||
                entry.ByteOffset >= existing.ByteOffset)
            {
                current[key] = entry;
            }
        }

        return current;
    }

    private static (string Session, string Message, string PartId) Identity(
        LiveActivityHistoryEntry entry) =>
        (entry.SessionId, entry.MessageId, entry.PartId);

    private static bool DigestEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
