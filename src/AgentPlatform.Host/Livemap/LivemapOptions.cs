namespace AgentPlatform.Host.Livemap;

public sealed class LivemapOptions
{
    public const string SectionName = "Livemap";

    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMinutes(1);

    public bool Enabled { get; set; }

    public string Repo { get; set; } = string.Empty;

    public string PinRepo { get; set; } = string.Empty;

    public string PinPath { get; set; } = string.Empty;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);
}
