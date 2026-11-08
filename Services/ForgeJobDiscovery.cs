using System.IO;
using DSMonitor.Models;
using DSMonitor.Parsing;

namespace DSMonitor.Services;

public sealed record ForgeDiscoveryResult(
    IReadOnlyList<ForgeJobMetadata> Jobs,
    bool DirectoryAvailable,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Discovers forge-delegate per-job metadata files. Missing directories,
/// malformed files and disappearing files are normal, non-fatal conditions.
/// </summary>
public sealed class ForgeJobDiscovery
{
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

        jobs.Sort((left, right) =>
        {
            var leftStarted = left.Started ?? DateTimeOffset.MinValue;
            var rightStarted = right.Started ?? DateTimeOffset.MinValue;
            var byStart = rightStarted.CompareTo(leftStarted);
            return byStart != 0
                ? byStart
                : string.Compare(left.JobId, right.JobId, StringComparison.OrdinalIgnoreCase);
        });

        return new ForgeDiscoveryResult(jobs, true, warnings);
    }
}
