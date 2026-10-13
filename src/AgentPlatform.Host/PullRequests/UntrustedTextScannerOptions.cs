namespace AgentPlatform.Host.PullRequests;

public sealed class UntrustedTextScannerOptions
{
    public const string SectionName = "TextScanner";

    public string BaseUrl { get; set; } = "http://litellm-llm-tool-guard.litellm.svc:8080";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(330);

    public TimeSpan RetryBudget { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
