using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;

namespace DSMonitor.Services;

/// <summary>Preflight ownership evidence for one metadata-bound source.</summary>
public sealed record LiveActivitySourceEvidence(
    string JobId,
    string? NormalizedPath,
    string? VerifiedPath,
    LedgerSourceIdentity PhysicalIdentity,
    IReadOnlyList<string> DuplicateOwners)
{
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(NormalizedPath) &&
        !string.IsNullOrWhiteSpace(VerifiedPath) &&
        PhysicalIdentity.IsVerified;
}

/// <summary>
/// Bounded, read-only projection of the metadata-bound Forge capture. It never
/// queries OpenCode stores or joins jobs by session, timestamps or directory.
/// A successful ledger observation is required; there is no direct-path read
/// fallback. Recomputing each scan avoids retaining stale content after
/// disappearance, replacement, truncation, or a change in ownership.
/// </summary>
public static class JobLiveActivityReader
{
    public const int MaxReadBytes = 64 * 1024;
    public const int MaxExcerptLength = 600;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedRootTypes = new(StringComparer.Ordinal)
    {
        "message-part", "step-finish", "step-start", "text", "tool", "tool-use"
    };
    private static readonly HashSet<string> SupportedPartTypes = new(StringComparer.Ordinal)
    {
        "step-finish", "step-start", "text", "tool", "tool-use"
    };

    /// <summary>
    /// Resolve opened source associations once per scan. Evidence includes the
    /// physical identity read from the opened handle and every other job that
    /// claims the same normalized path, verified path, or physical identity.
    /// </summary>
    public static IReadOnlyDictionary<string, LiveActivitySourceEvidence> Preflight(
        IReadOnlyList<ForgeJobMetadata> jobs,
        string runsRoot,
        CancellationToken cancellationToken = default)
    {
        var associations = new List<Association>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? normalizedPath = null;
            string? verifiedPath = null;
            var identity = default(LedgerSourceIdentity);
            if (job.HasExplicitLogPath && job.LogPath is { } logPath)
            {
                try
                {
                    normalizedPath = Path.GetFullPath(logPath);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    normalizedPath = null;
                }

                if (normalizedPath is not null)
                {
                    using var opened = SafeFileAccess.OpenRead(
                        logPath, runsRoot, rejectHardLinks: true);
                    if (opened.Stream is not null &&
                        opened.VerifiedPath is not null &&
                        opened.PhysicalIdentity.IsVerified)
                    {
                        verifiedPath = opened.VerifiedPath;
                        identity = opened.PhysicalIdentity;
                    }
                }
            }

            associations.Add(new Association(job, normalizedPath, verifiedPath, identity));
        }

        var result = new Dictionary<string, LiveActivitySourceEvidence>(StringComparer.Ordinal);
        foreach (var association in associations)
        {
            var owners = associations
                .Where(other => !string.Equals(
                    other.Job.JobId, association.Job.JobId, StringComparison.Ordinal))
                .Where(other => SameAssociation(association, other))
                .Select(other => other.Job.JobId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result[association.Job.JobId] = new LiveActivitySourceEvidence(
                association.Job.JobId,
                association.NormalizedPath,
                association.VerifiedPath,
                association.Identity,
                owners);
        }

        return result;
    }

    public static LiveActivitySnapshot Read(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default,
        long maxBytes = MaxReadBytes) =>
        Read(
            job,
            evidence,
            observation,
            runsRoot,
            observedAt,
            cancellationToken,
            maxBytes,
            beforeContentRead: null,
            beforePublishCheck: null);

    internal static LiveActivitySnapshot Read(
        ForgeJobMetadata job,
        LiveActivitySourceEvidence evidence,
        LedgerSourceObservation observation,
        string runsRoot,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        long maxBytes,
        Action? beforeContentRead,
        Action? beforePublishCheck)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(observation);

        if (job.IsProvisional || !job.HasExplicitLogPath || job.LogPath is null)
        {
            return Unavailable("N/A — explicit metadata log association unavailable.");
        }

        if (!TryNormalize(job.LogPath, out var jobPath) ||
            !TryNormalize(evidence.NormalizedPath, out var evidencePath) ||
            !string.Equals(jobPath, evidencePath, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("N/A — job/source association could not be verified.");
        }

        if (!string.Equals(evidence.JobId, job.JobId, StringComparison.Ordinal))
        {
            return Unavailable("N/A — preflight evidence belongs to a different job.");
        }

        if (!evidence.IsAvailable || evidence.DuplicateOwners is not { Count: 0 })
        {
            return Unavailable(
                "N/A — multiple jobs claim this source or its physical identity is unavailable; ownership is ambiguous.");
        }

        if (!string.Equals(observation.JobId, job.JobId, StringComparison.Ordinal) ||
            !TryNormalize(observation.SourcePath, out var observedPath) ||
            !string.Equals(observedPath, evidencePath, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("N/A — ingestion observation belongs to a different job or source.");
        }

        if (!observation.IsConsistent ||
            !observation.Identity.Equals(evidence.PhysicalIdentity))
        {
            return Unavailable(
                "N/A — source physical identity is unavailable, weak, or changed before ingestion.");
        }

        using var opened = SafeFileAccess.OpenRead(
            job.LogPath, runsRoot, rejectHardLinks: true);
        var verifiedPath = opened.VerifiedPath;
        if (opened.Stream is not { } stream ||
            verifiedPath is null ||
            !opened.PhysicalIdentity.Equals(observation.Identity) ||
            !string.Equals(
                verifiedPath,
                evidence.VerifiedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("N/A — job source unavailable or physical identity changed; no excerpt is shown.");
        }

        try
        {
            if (stream.Length < observation.CapturedLength)
            {
                return Unavailable(
                    "N/A — source length is below the captured boundary; no excerpt is shown.");
            }

            var count = (int)Math.Min(
                observation.CapturedLength,
                Math.Clamp(maxBytes, 1, MaxReadBytes));
            var start = observation.CapturedLength - count;
            var bytes = new byte[count];
            stream.Position = start;
            beforeContentRead?.Invoke();
            var read = 0;
            while (read < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = stream.Read(bytes, read, count - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read != count)
            {
                return Unavailable(
                    "N/A — the bounded capture could not be read completely; no excerpt is shown.");
            }

            ActivityExcerpt? doing = null;
            ActivityExcerpt? done = null;
            var texts = new Dictionary<(string Session, string Message), ActivityExcerpt>();
            // Discard the first record when starting mid-file, even if it
            // happens to be complete. Never parse a possibly partial record.
            var lineStart = start == 0 ? 0 : Array.IndexOf(bytes, (byte)'\n', 0, read) + 1;
            if (start > 0 && lineStart == 0)
            {
                return Unavailable("N/A — no complete record in bounded source window.");
            }

            for (var index = lineStart; index < read; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                cancellationToken.ThrowIfCancellationRequested();
                var offset = start + lineStart;
                var recordStart = lineStart;
                lineStart = index + 1;
                try
                {
                    var line = StrictUtf8.GetString(bytes, recordStart, index - recordStart)
                        .TrimStart('\uFEFF');
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("part", out var part) ||
                        part.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var partSession = Get(part, "sessionID") ?? Get(part, "sessionId");
                    var session = Get(root, "sessionID") ?? Get(root, "sessionId") ?? partSession;
                    if (partSession is not null &&
                        !string.Equals(partSession, session, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var message = Get(part, "messageID") ?? Get(part, "messageId");
                    if (!ValidId(session) || !ValidId(message)) continue;
                    if (!TryGetAssistantRole(root, part)) continue;
                    if (!TryGetActivityKind(root, part, out var isText, out var isTool, out var isFinish))
                    {
                        continue;
                    }

                    if (!ForgeEventParser.TryParse(line, job.JobId, observedAt, out var parsed) ||
                        parsed is null)
                    {
                        continue;
                    }

                    var key = (session!, message!);
                    var partId = Get(part, "id") ?? Get(part, "partID");
                    var sourcePath = verifiedPath;
                    ActivityExcerpt Excerpt(string text, string kind) => new(
                        Sanitize(text),
                        kind,
                        job.JobId,
                        session!,
                        message!,
                        ValidId(partId) ? partId : null,
                        sourcePath,
                        offset,
                        parsed.Event.Timestamp,
                        observedAt);
                    if (isText &&
                        Get(part, "text") is { } text &&
                        !string.IsNullOrWhiteSpace(text))
                    {
                        doing = Excerpt(text, "Assistant-reported text");
                        texts[key] = doing;
                    }
                    else if (isTool)
                    {
                        // The existing parser emits safe tool names/states,
                        // excluding command arguments, output and secrets.
                        doing = Excerpt(parsed.Event.Summary, "Observed tool activity");
                    }
                    else if (isFinish &&
                             Get(part, "reason") is "stop" or "end-turn" &&
                             texts.TryGetValue(key, out var result))
                    {
                        done = result with { Kind = "Agent-reported result — unverified" };
                    }
                }
                catch (JsonException) { /* Malformed/overlong/partial records are unavailable. */ }
                catch (DecoderFallbackException) { /* Invalid UTF-8 cannot establish source identity. */ }
            }

            beforePublishCheck?.Invoke();
            if (!SourceStillMatches(
                    job,
                    runsRoot,
                    observation,
                    verifiedPath,
                    stream,
                    out var reason))
            {
                return Unavailable(reason);
            }

            return new LiveActivitySnapshot
            {
                Doing = doing,
                Done = done,
                Availability = "Bounded source re-read; timestamps describe source records, not execution liveness."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unavailable("N/A — job source changed or became unavailable during read.");
        }
    }

    private static bool SourceStillMatches(
        ForgeJobMetadata job,
        string runsRoot,
        LedgerSourceObservation observation,
        string verifiedPath,
        FileStream stream,
        out string reason)
    {
        reason = "N/A — source changed before publication; all excerpts were discarded.";
        try
        {
            if (stream.Length != observation.CapturedLength)
            {
                return false;
            }

            using var recheck = SafeFileAccess.OpenRead(
                job.LogPath, runsRoot, rejectHardLinks: true);
            if (recheck.Stream is not { } reopened ||
                recheck.VerifiedPath is null ||
                !recheck.PhysicalIdentity.Equals(observation.Identity) ||
                !string.Equals(
                    recheck.VerifiedPath,
                    verifiedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                reopened.Length != observation.CapturedLength)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetAssistantRole(JsonElement root, JsonElement part)
    {
        if (!TryGetSingleRole(root, out var rootRole) ||
            !TryGetSingleRole(part, out var partRole))
        {
            return false;
        }

        if (rootRole is not null &&
            partRole is not null &&
            !string.Equals(rootRole, partRole, StringComparison.Ordinal))
        {
            return false;
        }

        var effective = partRole ?? rootRole;
        return string.Equals(effective, "assistant", StringComparison.Ordinal);
    }

    private static bool TryGetSingleRole(JsonElement value, out string? role)
    {
        role = null;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals("role")) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Value.ValueKind != JsonValueKind.String) return false;
            role = property.Value.GetString();
        }

        return true;
    }

    private static bool TryGetActivityKind(
        JsonElement root,
        JsonElement part,
        out bool isText,
        out bool isTool,
        out bool isFinish)
    {
        isText = false;
        isTool = false;
        isFinish = false;
        if (!TryGetSingleType(root, out var rootType) ||
            !TryGetSingleType(part, out var partType) ||
            rootType is null ||
            !SupportedRootTypes.Contains(rootType) ||
            (partType is not null && !SupportedPartTypes.Contains(partType)))
        {
            return false;
        }

        var rootCategory = Category(rootType);
        if (rootType == "message-part")
        {
            if (partType is null) return false;
            var wrappedCategory = Category(partType);
            if (wrappedCategory is null) return false;
            isText = wrappedCategory == "text";
            isTool = wrappedCategory == "tool";
            isFinish = wrappedCategory == "finish";
            return true;
        }

        if (rootCategory is null) return false;
        if (partType is not null && !string.Equals(Category(partType), rootCategory, StringComparison.Ordinal))
        {
            return false;
        }

        isText = rootCategory == "text";
        isTool = rootCategory == "tool";
        isFinish = rootCategory == "finish";
        return true;
    }

    private static bool TryGetSingleType(JsonElement value, out string? type)
    {
        type = null;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals("type")) continue;
            count++;
            if (count > 1) return false;
            if (property.Value.ValueKind == JsonValueKind.Null) return false;
            if (property.Value.ValueKind != JsonValueKind.String) return false;
            type = NormalizeType(property.Value.GetString());
        }

        return true;
    }

    private static string? Category(string type) => type switch
    {
        "text" => "text",
        "tool" or "tool-use" => "tool",
        "step-finish" => "finish",
        _ => null
    };

    private static string NormalizeType(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('_', '-').ToLowerInvariant();

    private static bool SameAssociation(Association left, Association right)
    {
        var samePath = left.NormalizedPath is not null &&
                       right.NormalizedPath is not null &&
                       string.Equals(
                           left.NormalizedPath,
                           right.NormalizedPath,
                           StringComparison.OrdinalIgnoreCase);
        var sameVerified = left.VerifiedPath is not null &&
                           right.VerifiedPath is not null &&
                           string.Equals(
                               left.VerifiedPath,
                               right.VerifiedPath,
                               StringComparison.OrdinalIgnoreCase);
        var sameIdentity = left.Identity.IsVerified &&
                           right.Identity.IsVerified &&
                           left.Identity.Equals(right.Identity);
        return samePath || sameVerified || sameIdentity;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static LiveActivitySnapshot Unavailable(string reason) => new() { Availability = reason };
    private static string? Get(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 && !value.Any(character => char.IsControl(character) ||
            char.GetUnicodeCategory(character) == UnicodeCategory.Format);

    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(MaxExcerptLength);
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                if (rune.Value is '\n' or '\r' or '\t') builder.Append(' ');
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > MaxExcerptLength) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private sealed record Association(
        ForgeJobMetadata Job,
        string? NormalizedPath,
        string? VerifiedPath,
        LedgerSourceIdentity Identity);
}
