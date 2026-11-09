using System.IO;
using DSMonitor.Models;
using DSMonitor.Parsing;

namespace DSMonitor.Services;

public sealed record ForgeDiscoveryResult(
    IReadOnlyList<ForgeJobMetadata> Jobs,
    bool DirectoryAvailable,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Bounded sample of top-level forge log files that could not be
    /// attributed to a discovered job. They are never used to invent jobs.
    /// </summary>
    public IReadOnlyList<string> UnassignedLogs { get; init; } = Array.Empty<string>();

    public int UnassignedLogCount { get; init; }

    public bool UnassignedLogScanTruncated { get; init; }
}

/// <summary>
/// Discovers forge-delegate per-job metadata files. Missing directories,
/// malformed files and disappearing files are normal, non-fatal conditions.
/// </summary>
public sealed class ForgeJobDiscovery
{
    private const int MaxUnassignedLogScan = 2048;
    private const int MaxReportedUnassignedLogs = 20;

    public ForgeJobDiscovery(string runsRoot)
    {
        RunsRoot = PathSafety.NormalizeRoot(runsRoot);
    }

    public string RunsRoot { get; }

    public ForgeDiscoveryResult Discover()
    {
        var jobs = new List<ForgeJobMetadata>();
        var warnings = new List<string>();
        var seenJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(RunsRoot))
        {
            warnings.Add("Forge runs directory is not available.");
            return new ForgeDiscoveryResult(jobs, false, warnings);
        }

        if (!SafeFileAccess.TryValidateTrustedRoot(RunsRoot, out var rootWarning))
        {
            warnings.Add(rootWarning ?? "Forge runs directory could not be verified safely.");
            return new ForgeDiscoveryResult(jobs, false, warnings);
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(RunsRoot, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("Forge runs directory could not be read.");
            return new ForgeDiscoveryResult(jobs, false, warnings);
        }

        try
        {
            foreach (var file in files)
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                ForgeJobMetadata metadata;
                try
                {
                    metadata = ForgeMetadataParser.ParseFile(file, RunsRoot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"A job metadata file could not be read: {Path.GetFileName(file)}");
                    continue;
                }

                if (!seenJobIds.Add(metadata.JobId))
                {
                    warnings.Add($"Duplicate job id '{metadata.JobId}' was ignored.");
                    continue;
                }

                jobs.Add(metadata);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("Forge runs directory changed while it was being scanned.");
        }

        var associatedLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (!string.IsNullOrWhiteSpace(job.LogPath))
            {
                // Only the metadata-selected path is associated. A different
                // jobId.log file is not silently assumed to belong to this job:
                // it is reported as unassigned so coverage cannot look clean.
                associatedLogs.Add(Path.GetFullPath(job.LogPath));
            }
        }

        var unassignedLogs = new List<string>();
        var unassignedCount = 0;
        var unassignedScanTruncated = false;
        try
        {
            var scanned = 0;
            foreach (var file in Directory.EnumerateFiles(
                         RunsRoot,
                         "*.log",
                         SearchOption.TopDirectoryOnly))
            {
                if (scanned++ >= MaxUnassignedLogScan)
                {
                    unassignedScanTruncated = true;
                    break;
                }

                if (!File.Exists(file))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(file);
                if (associatedLogs.Contains(fullPath))
                {
                    continue;
                }

                unassignedCount++;
                if (unassignedLogs.Count < MaxReportedUnassignedLogs)
                {
                    unassignedLogs.Add(fullPath);
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            warnings.Add("Unassigned forge log scan could not be completed; discovery coverage is incomplete.");
        }

        if (unassignedCount > 0)
        {
            warnings.Add(
                $"{unassignedCount} unassigned forge log file(s) were not attributed to a job; discovery coverage is incomplete.");
        }

        if (unassignedScanTruncated)
        {
            warnings.Add(
                $"Unassigned forge log scan was truncated after {MaxUnassignedLogScan} files; discovery coverage is incomplete.");
        }

        jobs.Sort((left, right) =>
        {
            var leftStarted = left.Started ?? DateTimeOffset.MinValue;
            var rightStarted = right.Started ?? DateTimeOffset.MinValue;
            var byStart = rightStarted.CompareTo(leftStarted);
            return byStart != 0
                ? byStart
                : string.Compare(left.JobId, right.JobId, StringComparison.OrdinalIgnoreCase);
        });

        return new ForgeDiscoveryResult(jobs, true, warnings)
        {
            UnassignedLogs = unassignedLogs,
            UnassignedLogCount = unassignedCount,
            UnassignedLogScanTruncated = unassignedScanTruncated
        };
    }
}
