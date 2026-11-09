namespace AgentPlatform.Host.Livemap;

public enum LivemapBuildState
{
    Detected,
    Done,
    Classified,
    NeedsOwner,
    Parked,
    Superseded,
}

public enum LivemapClassification
{
    Known,
    Easy,
}

public sealed class LivemapBuild
{
    public long BuildId { get; set; }

    public string ManifestGid { get; set; } = string.Empty;

    public DateTimeOffset SteamUpdatedAt { get; set; }

    public LivemapBuildState State { get; set; }

    public LivemapClassification? Classification { get; set; }

    public long? CompatRunId { get; set; }

    public string? Detail { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
