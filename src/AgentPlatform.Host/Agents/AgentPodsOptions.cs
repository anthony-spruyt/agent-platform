namespace AgentPlatform.Host.Agents;

public sealed class AgentPodsOptions
{
    public const string SectionName = "AgentPods";

    // renovate: datasource=docker depName=ghcr.io/anthony-spruyt/claude-agent-read
    public const string DefaultImage =
        "ghcr.io/anthony-spruyt/claude-agent-read:1.1.6@sha256:920bb6ee5061ba13e9a9830c4d94d8a6cad86b06626289e8823a00145a131540";

    public string Namespace { get; set; } = "agent-platform-agents";

    public string Image { get; set; } = DefaultImage;

    public string Model { get; set; } = "sonnet";

    public int MaxConcurrentActivities { get; set; } = 2;

    public int SchedulingGraceSeconds { get; set; } = AgentRunTimeouts.SchedulingGraceSeconds;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public string ServiceAccountName { get; set; } = "agent-runner";

    public string? ClusterReadServiceAccountName { get; set; }

    public string LiteLlmBaseUrl { get; set; } = "http://litellm.litellm.svc.cluster.local:4000";

    public int MaxTurns { get; set; } = 40;

    public decimal MaxBudgetUsd { get; set; } = 2m;

    public bool UseMcpHeadersHelper { get; set; }

    public string AgentGatewayUrl { get; set; } = "http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp";
}
