namespace AgentPlatform.Host.GitHub;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHub";

    public const string ReadAppSectionName = "GitHub:ReadApp";

    public string AppId { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;
}
