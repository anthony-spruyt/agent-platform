namespace AgentPlatform.Host.RenovateTriage;

public sealed class LiteLlmOptions
{
    public const string SectionName = "LiteLlm";

    public string BaseUrl { get; set; } = "http://litellm.litellm.svc.cluster.local:4000";

    public string ApiKey { get; set; } = string.Empty;

    public string JevPath { get; set; } = "v1/systemone";

    public string JevModel { get; set; } = "jev-1.14.0";
}
