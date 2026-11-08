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
    public string? Model { get; init; }
    public string? Directory { get; init; }
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public ForgeJobStatus ExplicitStatus { get; init; } = ForgeJobStatus.Unknown;
    public string? RawStatus { get; init; }
    public int? Pid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasExplicitStatus => ExplicitStatus != ForgeJobStatus.Unknown;
}
