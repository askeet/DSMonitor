using System.Globalization;
using System.IO;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Services;

namespace DSMonitor.Parsing;

/// <summary>
/// Defensive parser for forge-delegate per-job metadata. Every field is
/// optional. Missing status is Unknown; it is never inferred from PID or
/// from a deadline.
/// </summary>
public static class ForgeMetadataParser
{
    public static ForgeJobMetadata ParseFile(string metadataPath, string runsRoot)
    {
        var fileStem = Path.GetFileNameWithoutExtension(metadataPath);
        try
        {
            using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            return Parse(document.RootElement, metadataPath, fileStem, runsRoot);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            return CreateInvalid(metadataPath, fileStem, runsRoot, "Malformed job metadata; fields unavailable.");
        }
    }

    public static ForgeJobMetadata Parse(
        JsonElement root,
        string metadataPath,
        string fileStem,
        string runsRoot)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return CreateInvalid(metadataPath, fileStem, runsRoot, "Job metadata is not a JSON object.");
        }

        var warnings = new List<string>();
        var jobId = GetString(root, "id") ?? GetString(root, "jobId");
        if (string.IsNullOrWhiteSpace(jobId) || !PathSafety.IsSafeFileName(jobId))
        {
            warnings.Add("Job id is missing or invalid; using the metadata file name.");
            jobId = fileStem;
        }

        if (!PathSafety.IsSafeFileName(jobId))
        {
            jobId = "unknown-job";
        }

        var rawLogPath = GetString(root, "logPath") ?? GetString(root, "log_path");
        string? logPath;
        if (!string.IsNullOrWhiteSpace(rawLogPath))
        {
            var candidate = Path.IsPathRooted(rawLogPath)
                ? rawLogPath
                : Path.Combine(PathSafety.NormalizeRoot(runsRoot), rawLogPath);

            if (PathSafety.IsWithinRoot(candidate, runsRoot))
            {
                logPath = Path.GetFullPath(candidate);
            }
            else
            {
                warnings.Add("Metadata log path is outside the forge runs root and was ignored.");
                logPath = Path.Combine(PathSafety.NormalizeRoot(runsRoot), fileStem + ".log");
            }
        }
        else
        {
            logPath = Path.Combine(PathSafety.NormalizeRoot(runsRoot), fileStem + ".log");
        }

        var started = ParseTimestamp(root, "started", warnings);
        var deadline = ParseTimestamp(root, "deadline", warnings);

        var rawStatus = GetString(root, "status") ?? GetString(root, "state");
        var explicitStatus = ForgeJobStatus.Unknown;
        if (!string.IsNullOrWhiteSpace(rawStatus) &&
            !ForgeJobStatusExtensions.TryParse(rawStatus, out explicitStatus))
        {
            warnings.Add("Metadata status was not recognized; job status remains Unknown.");
            explicitStatus = ForgeJobStatus.Unknown;
        }

        var pid = GetInt32(root, "pid");

        return new ForgeJobMetadata
        {
            JobId = jobId,
            MetadataPath = metadataPath,
            LogPath = logPath,
            Model = GetString(root, "model"),
            Directory = GetString(root, "directory"),
            Started = started,
            Deadline = deadline,
            ExplicitStatus = explicitStatus,
            RawStatus = rawStatus,
            Pid = pid,
            Warnings = warnings
        };
    }

    private static ForgeJobMetadata CreateInvalid(
        string metadataPath,
        string fileStem,
        string runsRoot,
        string warning)
    {
        var safeId = PathSafety.IsSafeFileName(fileStem) ? fileStem : "unknown-job";
        return new ForgeJobMetadata
        {
            JobId = safeId,
            MetadataPath = metadataPath,
            LogPath = Path.Combine(PathSafety.NormalizeRoot(runsRoot), safeId + ".log"),
            ExplicitStatus = ForgeJobStatus.Unknown,
            Warnings = new[] { warning }
        };
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var number) ? number : null;
    }

    private static DateTimeOffset? ParseTimestamp(
        JsonElement root,
        string propertyName,
        List<string> warnings)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var milliseconds))
            {
                return FromUnixMilliseconds(milliseconds, propertyName, warnings);
            }

            if (value.TryGetDouble(out var fractionalMilliseconds))
            {
                return FromUnixMilliseconds((long)fractionalMilliseconds, propertyName, warnings);
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return FromUnixMilliseconds(milliseconds, propertyName, warnings);
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        warnings.Add($"Metadata field '{propertyName}' could not be parsed.");
        return null;
    }

    private static DateTimeOffset? FromUnixMilliseconds(
        long milliseconds,
        string propertyName,
        List<string> warnings)
    {
        if (milliseconds <= 0 ||
            milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            warnings.Add($"Metadata field '{propertyName}' is outside the supported range.");
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
