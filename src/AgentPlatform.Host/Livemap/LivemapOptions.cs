namespace AgentPlatform.Host.Livemap;

public sealed class LivemapOptions
{
    public const string SectionName = "Livemap";

    public const int MaxAgentRunsPerBuild = 10;

    public const int MinAgentDeadlineSeconds = 300;

    // The agent's GitHub token lives an hour, and the pod's scheduling grace has to fit inside it.
    public const int MaxAgentDeadlineSeconds = 3000;

    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMinutes(1);

    public bool Enabled { get; set; }

    public string Repo { get; set; } = string.Empty;

    public string PinRepo { get; set; } = string.Empty;

    public string PinPath { get; set; } = string.Empty;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    public int TrackingIssue { get; set; }

    public bool AutoMerge { get; set; }

    public List<string> RequiredChecks { get; set; } = [];

    public string ReleaseBot { get; set; } = string.Empty;

    public List<string> ReleaseRequiredChecks { get; set; } = [];

    public bool HardPath { get; set; }

    public string AgentModel { get; set; } = "opus";

    public int AgentMaxTurns { get; set; } = 200;

    public decimal AgentMaxBudgetUsd { get; set; } = 25m;

    public int AgentRunsPerBuild { get; set; } = 3;

    public int AgentDeadlineSeconds { get; set; } = 2700;

    public string DepotDownloaderUrl { get; set; } = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-linux-x64.zip";

    public string DepotDownloaderSha256 { get; set; } = "a999dec66b4850fc961bd50366696d23c2d0fad7b18790e6a5647b2f19097a53";

    public string PythonIndexUrl { get; set; } = "http://nexus.nexus-system.svc.cluster.local:8081/repository/pypi-proxy/simple";

    public string CapstoneVersion { get; set; } = "5.0.9";

    public string CapstoneSha256 { get; set; } = "273fd8d747d2e35c88f91450be51a603ecfaafb00d96d9f315dcb8689c86193e";
}
