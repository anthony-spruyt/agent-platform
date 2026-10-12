namespace AgentPlatform.Host.RenovateTriage;

public sealed class LiteLlmOptions
{
    public const string SectionName = "LiteLlm";

    public string BaseUrl { get; set; } = "http://litellm.litellm.svc.cluster.local:4000";

    public string ApiKey { get; set; } = string.Empty;
}
