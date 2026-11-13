using System.Security.Cryptography;
using System.Text;
using DSMonitor.Models;

namespace DSMonitor.Services;

internal enum OriginalRequestEvidenceState
{
    None,
    Valid,
    Conflicting,
    Unavailable
}

internal readonly record struct OriginalRequestEvidenceSelection(
    OriginalRequestEvidenceState State,
    IReadOnlyList<string> MessageIds,
    string Fingerprint)
{
    public bool IsUsable => State is OriginalRequestEvidenceState.None or
        OriginalRequestEvidenceState.Valid;
}

/// <summary>
/// Bounded job-local evidence for Original Request correlation. It reads only
/// the selected job's already-guarded, transient Live Activity projection: no
/// file, JSONL, database, other job, or other session is read. Structured
/// message ids are accepted when the guarded reader recorded either verified
/// assistant role or a roleless part; roleless ids remain candidates and the
/// OpenCode API must confirm them as assistant messages. Explicit user-role
/// records are excluded by the guarded reader. A missing/inconsistent source
/// projection is explicit unavailable evidence, not the healthy no-anchor state.
/// </summary>
internal static class OriginalRequestEvidence
{
    internal const int MaxMessageIds = 256;

    // These are the role-verified assistant and roleless kinds emitted by the
    // guarded Live Activity reader. Roleless ids are preserved because pinned
    // OpenCode/Forge payloads can omit an explicit role while retaining
    // part.messageID; API role confirmation is still required.
    private const string AssistantTextKind = "Assistant-reported text";
    private const string AssistantToolKind = "Observed tool activity";
    private const string AssistantResultKind = "Agent-reported result — unverified";
    private const string RolelessTextKind = "Observed roleless text — unverified";
    private const string RolelessToolKind = "Observed roleless tool activity — unverified";
    private const string RolelessResultKind = "Observed roleless result — unverified";

    internal static OriginalRequestEvidenceSelection Select(
        ForgeJobSnapshot snapshot,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.LiveActivity.IsHealthy)
        {
            return new OriginalRequestEvidenceSelection(
                OriginalRequestEvidenceState.Unavailable,
                Array.Empty<string>(),
                ComputeStateFingerprint(
                    "unavailable",
                    snapshot.JobId,
                    sessionId ?? string.Empty,
                    Array.Empty<string>()));
        }

        if (!OpenCodeIdentifiers.IsSafeSessionId(sessionId) ||
            !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new OriginalRequestEvidenceSelection(
                OriginalRequestEvidenceState.None,
                Array.Empty<string>(),
                string.Empty);
        }

        var session = sessionId!;
        var collected = new HashSet<string>(StringComparer.Ordinal);
        var rawIds = new HashSet<string>(StringComparer.Ordinal);
        string? sourcePath = null;
        var rejected = false;
        var candidateCount = 0;

        void Consider(
            string? jobId,
            string? entrySessionId,
            string? messageId,
            string? kind,
            string? entrySourcePath)
        {
            if (!IsEvidenceKind(kind))
            {
                return;
            }

            candidateCount++;
            if (OpenCodeIdentifiers.IsSafeMessageId(messageId))
            {
                rawIds.Add(messageId!);
            }

            if (!string.Equals(jobId, snapshot.JobId, StringComparison.Ordinal) ||
                !string.Equals(entrySessionId, session, StringComparison.Ordinal) ||
                !OpenCodeIdentifiers.IsSafeMessageId(messageId) ||
                string.IsNullOrWhiteSpace(entrySourcePath))
            {
                rejected = true;
                return;
            }

            if (sourcePath is null)
            {
                sourcePath = entrySourcePath;
            }
            else if (!string.Equals(sourcePath, entrySourcePath, StringComparison.OrdinalIgnoreCase))
            {
                rejected = true;
                return;
            }

            collected.Add(messageId!);
        }

        var activity = snapshot.LiveActivity;
        ConsiderExcerpt(activity.Doing, Consider);
        ConsiderExcerpt(activity.Done, Consider);
        ConsiderExcerpt(activity.LatestText, Consider);
        foreach (var entry in activity.History)
        {
            Consider(
                entry.JobId,
                entry.SessionId,
                entry.MessageId,
                entry.Kind,
                entry.SourcePath);
        }

        if (rejected || collected.Count > MaxMessageIds)
        {
            // The projection normally contains only this job/session/source.
            // If it does not, the evidence is explicitly conflicting rather
            // than silently becoming the healthy no-anchor state.
            return new OriginalRequestEvidenceSelection(
                OriginalRequestEvidenceState.Conflicting,
                Array.Empty<string>(),
                ComputeStateFingerprint(
                    "conflict",
                    snapshot.JobId,
                    session,
                    rawIds));
        }

        if (candidateCount == 0)
        {
            return new OriginalRequestEvidenceSelection(
                OriginalRequestEvidenceState.None,
                Array.Empty<string>(),
                string.Empty);
        }

        var ordered = collected
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return new OriginalRequestEvidenceSelection(
            OriginalRequestEvidenceState.Valid,
            ordered,
            ComputeFingerprint(snapshot.JobId, session, ordered));
    }

    internal static IReadOnlyList<string> Collect(
        ForgeJobSnapshot snapshot,
        string? sessionId)
    {
        var selection = Select(snapshot, sessionId);
        return selection.State == OriginalRequestEvidenceState.Valid
            ? selection.MessageIds
            : Array.Empty<string>();
    }

    internal static bool TryCollect(
        ForgeJobSnapshot snapshot,
        string? sessionId,
        out IReadOnlyList<string> ids)
    {
        var selection = Select(snapshot, sessionId);
        ids = selection.State == OriginalRequestEvidenceState.Valid
            ? selection.MessageIds
            : Array.Empty<string>();
        return selection.IsUsable;
    }

    internal static string ComputeFingerprint(
        string jobId,
        string sessionId,
        IReadOnlyList<string>? messageIds)
    {
        if (messageIds is null || messageIds.Count == 0)
        {
            return string.Empty;
        }

        return ComputeStateFingerprint("valid", jobId, sessionId, messageIds);
    }

    internal static bool IsVerifiedAssistantKind(string? kind) =>
        kind is AssistantTextKind or AssistantToolKind or AssistantResultKind;

    internal static bool IsRolelessKind(string? kind) =>
        kind is RolelessTextKind or RolelessToolKind or RolelessResultKind;

    private static bool IsEvidenceKind(string? kind) =>
        IsVerifiedAssistantKind(kind) || IsRolelessKind(kind);

    private static string ComputeStateFingerprint(
        string state,
        string jobId,
        string sessionId,
        IEnumerable<string> messageIds)
    {
        var builder = new StringBuilder();
        builder.Append(state).Append('\n').Append(jobId).Append('\n').Append(sessionId);
        foreach (var id in messageIds
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            builder.Append('\n').Append(id);
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void ConsiderExcerpt(
        ActivityExcerpt? excerpt,
        Action<string?, string?, string?, string?, string?> consider)
    {
        if (excerpt is null)
        {
            return;
        }

        consider(
            excerpt.JobId,
            excerpt.SessionId,
            excerpt.MessageId,
            excerpt.Kind,
            excerpt.SourcePath);
    }
}
