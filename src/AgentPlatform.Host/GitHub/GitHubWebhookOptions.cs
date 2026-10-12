namespace AgentPlatform.Host.GitHub;

public sealed class GitHubWebhookOptions
{
    public const string SectionName = "GitHub";

    public string WebhookSecret { get; set; } = string.Empty;
}
