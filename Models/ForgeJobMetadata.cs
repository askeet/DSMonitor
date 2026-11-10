namespace DSMonitor.Models;

/// <summary>
/// Metadata read from a forge-delegate per-job JSON file. Forge 1.2.4 writes
/// id, pid, model, directory, started, deadline and logPath. Status is not
/// guaranteed; a missing status remains Unknown.
/// </summary>
public sealed record ForgeJobMetadata
{
    public required string JobId { get; init; }
    public required string MetadataPath { get; init; }
    public string? LogPath { get; init; }
    /// <summary>True only for an explicit in-root metadata log association.</summary>
    public bool HasExplicitLogPath { get; init; }
    public string? Model { get; init; }
    public string? Directory { get; init; }
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public ForgeJobStatus ExplicitStatus { get; init; } = ForgeJobStatus.Unknown;
    public string? RawStatus { get; init; }
    public int? Pid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the job identity came from an approved file-name fallback or
    /// malformed metadata rather than a verified forge job id. Provisional
    /// identities are retained for safe monitoring but always report incomplete
    /// durable coverage.
    /// </summary>
    public bool IsProvisional { get; init; }

    public bool HasExplicitStatus => ExplicitStatus != ForgeJobStatus.Unknown;
}
