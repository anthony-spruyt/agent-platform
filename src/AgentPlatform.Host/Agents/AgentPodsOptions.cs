namespace AgentPlatform.Host.Agents;

public sealed class AgentPodsOptions
{
    public const string SectionName = "AgentPods";

    public const string DefaultImage =
        "ghcr.io/anthony-spruyt/claude-agent-read:1.0.109@sha256:e25e2704535d2bbe07332e764b1c8265c624b64c45bf206f4935591fe4947cef";

    public string Namespace { get; set; } = "agent-platform-agents";

    public string Image { get; set; } = DefaultImage;

    public string Model { get; set; } = "sonnet";

    public int MaxConcurrentActivities { get; set; } = 2;

    public int SchedulingGraceSeconds { get; set; } = AgentRunTimeouts.SchedulingGraceSeconds;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public string ServiceAccountName { get; set; } = "agent-runner";

    public string? ClusterReadServiceAccountName { get; set; }

    public string LiteLlmBaseUrl { get; set; } = "http://litellm.litellm.svc.cluster.local:4000";

    public string AgentGatewayUrl { get; set; } = "http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp";
}
