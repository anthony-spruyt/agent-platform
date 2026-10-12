namespace AgentPlatform.Host.RenovateTriage;

public sealed class LiteLlmOptions
{
    public const string SectionName = "LiteLlm";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}
