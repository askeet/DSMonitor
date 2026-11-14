using DSMonitor.Models;

namespace DSMonitor.Services;

/// <summary>
/// One display row source: a physical record plus, for step rows, the pairing
/// decision derived from the retained records. <see cref="Entry"/> is always
/// the record that owns the row identity and its own offset/digest/timestamp.
/// </summary>
internal readonly record struct ProjectedActivity(
    LiveActivityHistoryEntry Entry,
    LiveActivityStepPair? Step);

/// <summary>
/// Derives the display projection from the already bounded, continuity-checked
/// and window-evicted history entries. It performs no source read, no
/// persistence, and no identity rewriting: every row still corresponds to one
/// retained physical record, and a merged step row keeps the start record as
/// its identity while carrying the exact finish record as extra provenance.
///
/// Pairing rules (all ordinal, fail closed):
/// - only exact job/session/message groups are considered; never across them;
/// - records are ordered by absolute source byte offset, never by timestamps,
///   text, or part-id matching;
/// - an unambiguous sequential start followed by a finish pairs;
/// - a repeated start, an identity shared by more than one step record, two
///   records claiming one offset, or a reader-reported identity conflict makes
///   the affected records ambiguous and unpairable;
/// - a finish without a visible start in the window stays an unmatched finish,
///   and a start without a finish stays an unmatched start.
/// </summary>
internal static class LiveActivityHistoryProjection
{
    public static IReadOnlyList<ProjectedActivity> Merge(
        IEnumerable<LiveActivityHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var source = entries as IReadOnlyCollection<LiveActivityHistoryEntry> ??
                     entries.ToArray();
        if (source.Count == 0)
        {
            return Array.Empty<ProjectedActivity>();
        }

        var projected = new List<ProjectedActivity>(source.Count);
        var stepGroups = new Dictionary<
            (string Job, string Session, string Message),
            List<LiveActivityHistoryEntry>>();
        foreach (var entry in source)
        {
            if (!IsStep(entry))
            {
                projected.Add(new ProjectedActivity(entry, null));
                continue;
            }

            var key = (entry.JobId, entry.SessionId, entry.MessageId);
            if (!stepGroups.TryGetValue(key, out var group))
            {
                group = new List<LiveActivityHistoryEntry>();
                stepGroups[key] = group;
            }

            group.Add(entry);
        }

        foreach (var group in stepGroups.Values)
        {
            PairGroup(group, projected);
        }

        return LiveActivityHistoryPolicy.Order(projected, item => item.Entry);
    }

    /// <summary>
    /// Resolves the effective activity class of one entry. Production entries
    /// always carry the explicit discriminator; the textual fallback exists
    /// only for synthetic/older entries that predate it and never yields a
    /// step class.
    /// </summary>
    public static LiveActivityKind ResolveKind(LiveActivityHistoryEntry entry) =>
        entry.ActivityKind != LiveActivityKind.Unknown
            ? entry.ActivityKind
            : InferLegacyKind(entry.Kind);

    public static bool IsStep(LiveActivityHistoryEntry entry) =>
        ResolveKind(entry) is LiveActivityKind.StepStart or LiveActivityKind.StepFinish;

    private static LiveActivityKind InferLegacyKind(string kind)
    {
        if (kind.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            return LiveActivityKind.Tool;
        }

        return kind.Contains("text", StringComparison.OrdinalIgnoreCase)
            ? LiveActivityKind.Text
            : LiveActivityKind.Unknown;
    }

    private static void PairGroup(
        List<LiveActivityHistoryEntry> group,
        List<ProjectedActivity> output)
    {
        var ordered = group
            .OrderBy(entry => entry.ByteOffset)
            .ThenBy(entry => entry.PartId, StringComparer.Ordinal)
            .ToArray();

        var duplicateOffsets = new HashSet<long>();
        var seenOffsets = new HashSet<long>();
        var identityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in ordered)
        {
            if (!seenOffsets.Add(record.ByteOffset))
            {
                duplicateOffsets.Add(record.ByteOffset);
            }

            identityCounts[record.PartId] =
                identityCounts.TryGetValue(record.PartId, out var count) ? count + 1 : 1;
        }

        LiveActivityHistoryEntry? pendingStart = null;
        void EmitAmbiguous(LiveActivityHistoryEntry record) =>
            output.Add(new ProjectedActivity(
                record,
                LiveActivityStepPair.Single(record, LiveActivityStepState.Ambiguous)));

        foreach (var record in ordered)
        {
            var ambiguous =
                record.StepIdentityConflict ||
                duplicateOffsets.Contains(record.ByteOffset) ||
                identityCounts[record.PartId] > 1 ||
                record.ActivityKind is not (LiveActivityKind.StepStart or LiveActivityKind.StepFinish);
            if (ambiguous)
            {
                if (pendingStart is not null)
                {
                    EmitAmbiguous(pendingStart);
                    pendingStart = null;
                }

                EmitAmbiguous(record);
                continue;
            }

            if (record.ActivityKind == LiveActivityKind.StepStart)
            {
                // A second start before any finish cannot be paired
                // unambiguously. Both starts fail closed and the following
                // finish is not matched to either of them.
                if (pendingStart is not null)
                {
                    EmitAmbiguous(pendingStart);
                    EmitAmbiguous(record);
                    pendingStart = null;
                    continue;
                }

                pendingStart = record;
                continue;
            }

            if (pendingStart is not null)
            {
                var pair = new LiveActivityStepPair(
                    pendingStart,
                    record,
                    LiveActivityStepState.Paired);
                output.Add(new ProjectedActivity(pair.Entry, pair));
                pendingStart = null;
            }
            else
            {
                output.Add(new ProjectedActivity(
                    record,
                    LiveActivityStepPair.Single(record, LiveActivityStepState.UnmatchedFinish)));
            }
        }

        if (pendingStart is not null)
        {
            output.Add(new ProjectedActivity(
                pendingStart,
                LiveActivityStepPair.Single(pendingStart, LiveActivityStepState.UnmatchedStart)));
        }
    }
}
