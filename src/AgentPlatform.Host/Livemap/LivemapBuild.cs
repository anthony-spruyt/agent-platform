namespace AgentPlatform.Host.Livemap;

public enum LivemapBuildState
{
    Detected,
    Done,
    Classified,
    NeedsOwner,
    Parked,
    Superseded,
    PullRequestOpen,
    PullRequestWaiting,
    Merging,
    Merged,
    ReleaseWaiting,
    Released,
    Verified,
    PinOpen,
    PinWaiting,
    Pinned,
    Patching,
    OwnerReview,
}

public enum LivemapClassification
{
    Known,
    Easy,
    Hard,
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

    public int? PullRequestNumber { get; set; }

    public string? HeadSha { get; set; }

    public string? ReleaseTag { get; set; }

    public string? ReleaseSha256 { get; set; }

    public int? PinPullRequestNumber { get; set; }

    public string? PinHeadSha { get; set; }

    public int PinAttempts { get; set; }

    public DateTimeOffset? MergedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public DateTimeOffset? PinnedAt { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }

    public int Attempts { get; set; }

    public int AgentRuns { get; set; }

    public string? PatchNotes { get; set; }

    public string? PatchResult { get; set; }

    public string? PatchBaseSha { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
