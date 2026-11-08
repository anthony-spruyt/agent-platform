namespace AgentPlatform.Host.Livemap;

public enum LivemapBuildState
{
    Detected,
    Done,
}

public sealed class LivemapBuild
{
    public long BuildId { get; set; }

    public string ManifestGid { get; set; } = string.Empty;

    public DateTimeOffset SteamUpdatedAt { get; set; }

    public LivemapBuildState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
