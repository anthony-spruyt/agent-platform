namespace AgentPlatform.Host.RenovateTriage;

public sealed class RenovateTriageOptions
{
    public const string SectionName = "RenovateTriage";

    public TriageMode Mode { get; set; } = TriageMode.Shadow;
}
