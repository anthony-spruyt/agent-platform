using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace AgentPlatform.Host.Agents;

public sealed class AgentPodSpecBuilder(AgentPodsOptions options)
{
    public const string ManagedByLabel = "managed-by";
    public const string ManagedByValue = "agent-platform";
    public const string WorkflowIdAnnotation = "agent-platform/workflow-id";
    public const string RunIdAnnotation = "agent-platform/run-id";
    public const string AgentContainerName = "claude-code";
    public const string CredentialsSecretName = "agent-credentials";
    public const string SessionTokenKey = "session-token";
    public const int MaxDeadlineSeconds = 3600;

    private const string ClaudeHome = "claude-home";
    private const string Workspace = "workspace";
    private const long AgentUserId = 1000;

    private const string ResultInstruction =
        "When you have finished, call the mcp__agentplatform__submit_result tool exactly once with your final result. " +
        "Do not finish without calling it.";

    private static readonly JsonSerializerOptions _mcpJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public V1Pod Build(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeadlineSeconds is < 1 or > MaxDeadlineSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.DeadlineSeconds, $"The pod deadline must be between 1 and {MaxDeadlineSeconds} seconds.");
        }

        var tier = TierSettings.For(request.Tier, options);
        var name = AgentPodNaming.PodName(request.WorkflowId, request.RunId, request.Step);

        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string>
                {
                    [ManagedByLabel] = ManagedByValue,
                    ["app.kubernetes.io/name"] = "agent-platform-agent",
                    ["agent-platform/tier"] = tier.Label,
                },
                Annotations = new Dictionary<string, string>
                {
                    [WorkflowIdAnnotation] = request.WorkflowId,
                    [RunIdAnnotation] = request.RunId,
                },
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                ServiceAccountName = tier.ServiceAccount,
                AutomountServiceAccountToken = tier.MountServiceAccountToken,
                PriorityClassName = tier.PriorityClass,
                ActiveDeadlineSeconds = request.DeadlineSeconds,
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = AgentUserId,
                    RunAsGroup = AgentUserId,
                    FsGroup = AgentUserId,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
                },
                Containers = [BuildContainer(request, name, tier)],
                Volumes =
                [
                    new V1Volume { Name = ClaudeHome, EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("256Mi") } },
                    new V1Volume { Name = Workspace, EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("1Gi") } },
                ],
            },
        };
    }

    private V1Container BuildContainer(AgentRunRequest request, string podName, TierSettings tier) => new()
    {
        Name = AgentContainerName,
        Image = options.Image,
        Command = ["claude"],
        Args = BuildArgs(request),
        WorkingDir = "/workspace",
        Env = BuildEnv(podName, tier),
        Resources = new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new("250m"),
                ["memory"] = new("1Gi"),
            },
            Limits = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new(tier.CpuLimit),
                ["memory"] = new("2Gi"),
            },
        },
        SecurityContext = new V1SecurityContext
        {
            AllowPrivilegeEscalation = false,
            ReadOnlyRootFilesystem = false,
            RunAsNonRoot = true,
            Capabilities = new V1Capabilities { Drop = ["ALL"] },
        },
        VolumeMounts =
        [
            new V1VolumeMount { Name = ClaudeHome, MountPath = "/home/node/.claude" },
            new V1VolumeMount { Name = Workspace, MountPath = "/workspace" },
        ],
    };

    private List<string> BuildArgs(AgentRunRequest request) =>
    [
        "-p",
        EscapeKubernetesReferences(request.Prompt),
        "--output-format",
        "stream-json",
        "--verbose",
        "--permission-mode",
        "bypassPermissions",
        "--model",
        options.Model,
        "--append-system-prompt",
        ResultInstruction,
        "--mcp-config",
        BuildMcpConfig(),
    ];

    private string BuildMcpConfig()
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["agentplatform"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = options.AgentGatewayUrl,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer $(AGENT_SESSION_TOKEN)" },
                },
                ["litellm"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = $"{options.LiteLlmBaseUrl.TrimEnd('/')}/mcp",
                    ["headers"] = new JsonObject { ["x-litellm-api-key"] = "Bearer $(LITELLM_API_KEY)" },
                },
            },
        };

        return config.ToJsonString(_mcpJson);
    }

    private List<V1EnvVar> BuildEnv(string podName, TierSettings tier) =>
    [
        Literal("HOME", "/home/node"),
        FromSecret("CLAUDE_CODE_OAUTH_TOKEN", CredentialsSecretName, "claude-code-oauth-token"),
        FromSecret("LITELLM_API_KEY", CredentialsSecretName, tier.LiteLlmKey),
        FromSecret("AGENT_SESSION_TOKEN", podName, SessionTokenKey),
        Literal("ANTHROPIC_BASE_URL", options.LiteLlmBaseUrl),
        Literal("ANTHROPIC_CUSTOM_HEADERS", "x-litellm-api-key: Bearer $(LITELLM_API_KEY)"),
        Literal("CLAUDE_CODE_ATTRIBUTION_HEADER", "0"),
        Literal("ENABLE_TOOL_SEARCH", "true"),
        Literal("MCP_TIMEOUT", "30000"),
        Literal("DISABLE_AUTOUPDATER", "1"),
        Literal("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1"),
        Literal("SAFE_CHAIN_LOGGING", "silent"),
        Literal("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
        Literal("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
        Literal("OTEL_LOG_TOOL_DETAILS", "1"),
        Literal("OTEL_LOG_TOOL_CONTENT", "1"),
        Literal("OTEL_LOG_USER_PROMPTS", "1"),
        Literal("OTEL_METRICS_EXPORTER", "otlp"),
        Literal("OTEL_LOGS_EXPORTER", "otlp"),
        Literal("OTEL_TRACES_EXPORTER", "otlp"),
        Literal("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
        Literal("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://vmsingle-victoria-metrics-k8s-stack.observability.svc:8428/opentelemetry/v1/metrics"),
        Literal("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "http://victoria-logs-single-server.observability.svc:9428/insert/opentelemetry/v1/logs"),
        Literal("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://victoria-traces-single-vt-single-server.observability.svc:10428/insert/opentelemetry/v1/traces"),
        Literal("OTEL_RESOURCE_ATTRIBUTES", $"agent.namespace={options.Namespace},agent.tier={tier.Label}"),
    ];

    private static V1EnvVar Literal(string name, string value) => new() { Name = name, Value = value };

    private static V1EnvVar FromSecret(string name, string secret, string key) => new()
    {
        Name = name,
        ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = secret, Key = key } },
    };

    private static string EscapeKubernetesReferences(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    private sealed record TierSettings(
        string Label,
        string LiteLlmKey,
        string ServiceAccount,
        bool MountServiceAccountToken,
        string PriorityClass,
        string CpuLimit)
    {
        public static TierSettings For(AgentTier tier, AgentPodsOptions options) => tier switch
        {
            AgentTier.Read => new TierSettings("read", "litellm-key-read", options.ServiceAccountName, false, "low-priority", "500m"),
            AgentTier.ClusterRead => new TierSettings(
                "cluster-read",
                "litellm-key-cluster-read",
                options.ClusterReadServiceAccountName
                    ?? throw new AgentConfigurationException("AgentPods:ClusterReadServiceAccountName must be set to run the cluster-read tier."),
                true,
                "high-priority",
                "1250m"),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown agent tier."),
        };
    }
}
