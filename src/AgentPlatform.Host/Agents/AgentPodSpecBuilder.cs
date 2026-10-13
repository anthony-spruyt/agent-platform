using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
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
    public const string McpConfigKey = "mcp-config.json";
    public const string PromptKey = "prompt.txt";
    public const string GitHubTokenKey = "github-token";
    public const string GitCredentialKey = "git-credentials";
    public const string McpSessionKeyEnv = "AP_MCP_SESSION_KEY";
    public const string LiteLlmKeyEnv = "AP_LITELLM_API_KEY";
    public const string McpHeadersHelper = "/usr/local/bin/ap-mcp-headers";
    public const string PluginBootstrapCommand = "/usr/local/bin/claude-plugins-bootstrap";
    public const string CloneInitContainerName = "git-clone";
    public const string PluginsInitContainerName = "plugins";
    public const string RepoDirectory = "/workspace/repo";
    public const string PullRequestDirectory = "/workspace/pr";
    public const int MaxDeadlineSeconds = 3600;

    private const string ClaudeHome = "claude-home";
    private const string Workspace = "workspace";
    private const string McpConfigVolume = "mcp-config";
    private const string PromptVolume = "prompt";
    private const string GitCredentialsVolume = "git-credentials";
    private const string McpConfigDirectory = "/run/ap/mcp";
    private const string McpConfigFile = "mcp.json";
    private const string PromptDirectory = "/run/ap/prompt";
    private const string GitCredentialsDirectory = "/run/ap/git";
    private const int SecretFileMode = 0b100_100_000;
    private const long AgentUserId = 1000;

    private const string CloneScript =
        "set -eu\n" +
        "repo=\"$1\"\n" +
        "sha=\"$2\"\n" +
        "url=\"https://github.com/${repo}.git\"\n" +
        "helper=\"store --file=" + GitCredentialsDirectory + "/" + GitCredentialKey + "\"\n" +
        "git -c credential.helper= -c \"credential.helper=${helper}\" clone --quiet --depth 1 \"${url}\" " + RepoDirectory + "\n" +
        "git init --quiet " + PullRequestDirectory + "\n" +
        "git -C " + PullRequestDirectory + " -c credential.helper= -c \"credential.helper=${helper}\" fetch --quiet --depth 1 \"${url}\" \"${sha}\"\n" +
        "git -C " + PullRequestDirectory + " -c advice.detachedHead=false checkout --quiet FETCH_HEAD";

    private const string StdinLauncher = "exec claude \"$@\" < " + PromptDirectory + "/" + PromptKey;

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

        var repo = ValidateRepo(request.Repo);
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
                InitContainers = repo is null ? null : [BuildCloneContainer(repo), BuildPluginsContainer()],
                Containers = [BuildContainer(name, tier, repo)],
                Volumes = BuildVolumes(name, repo),
            },
        };
    }

    public IReadOnlyDictionary<string, string> BuildStaticSecretData(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Dictionary<string, string>
        {
            [McpConfigKey] = BuildMcpConfig(),
            [PromptKey] = request.Prompt,
        };
    }

    public string BuildMcpConfig()
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["agentplatform"] = McpServer(options.AgentGatewayUrl, "Authorization", $"Bearer ${{{McpSessionKeyEnv}}}"),
                ["litellm"] = McpServer($"{options.LiteLlmBaseUrl.TrimEnd('/')}/mcp", "x-litellm-api-key", $"Bearer ${{{LiteLlmKeyEnv}}}"),
            },
        };

        return config.ToJsonString(_mcpJson);
    }

    private JsonObject McpServer(string url, string header, string value)
    {
        var server = new JsonObject { ["type"] = "http", ["url"] = url };
        if (options.UseMcpHeadersHelper)
        {
            server["headersHelper"] = McpHeadersHelper;
        }
        else
        {
            server["headers"] = new JsonObject { [header] = value };
        }

        return server;
    }

    private static AgentRepoContext? ValidateRepo(AgentRepoContext? repo)
    {
        if (repo is null)
        {
            return null;
        }

        GitHubRepoName.Parse(repo.Repo, nameof(repo));
        if (repo.PullRequest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repo), repo.PullRequest, "The pull request number must be positive.");
        }

        if (repo.HeadSha.Length != 40 || !repo.HeadSha.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The head sha must be 40 hex characters.", nameof(repo));
        }

        return repo;
    }

    private static List<V1Volume> BuildVolumes(string podName, AgentRepoContext? repo)
    {
        List<V1Volume> volumes =
        [
            new() { Name = ClaudeHome, EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("256Mi") } },
            new() { Name = Workspace, EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("1Gi") } },
            SecretFile(McpConfigVolume, podName, McpConfigKey, McpConfigFile),
            SecretFile(PromptVolume, podName, PromptKey, PromptKey),
        ];
        if (repo is not null)
        {
            volumes.Add(SecretFile(GitCredentialsVolume, podName, GitCredentialKey, GitCredentialKey));
        }

        return volumes;
    }

    private static V1Volume SecretFile(string volume, string secret, string key, string path) => new()
    {
        Name = volume,
        Secret = new V1SecretVolumeSource
        {
            SecretName = secret,
            DefaultMode = SecretFileMode,
            Items = [new V1KeyToPath { Key = key, Path = path }],
        },
    };

    private V1Container BuildCloneContainer(AgentRepoContext repo) => new()
    {
        Name = CloneInitContainerName,
        Image = options.Image,
        Command = ["sh", "-c", CloneScript, CloneInitContainerName, repo.Repo, repo.HeadSha],
        WorkingDir = "/workspace",
        Env = [Literal("HOME", "/home/node"), Literal("GIT_TERMINAL_PROMPT", "0")],
        Resources = InitResources(),
        SecurityContext = RestrictedContext(),
        VolumeMounts =
        [
            new V1VolumeMount { Name = Workspace, MountPath = "/workspace" },
            new V1VolumeMount { Name = GitCredentialsVolume, MountPath = GitCredentialsDirectory, ReadOnlyProperty = true },
        ],
    };

    private V1Container BuildPluginsContainer() => new()
    {
        Name = PluginsInitContainerName,
        Image = options.Image,
        Command = [PluginBootstrapCommand, RepoDirectory],
        WorkingDir = RepoDirectory,
        Env = [Literal("HOME", "/home/node"), Literal("SAFE_CHAIN_LOGGING", "silent"), Literal("DISABLE_AUTOUPDATER", "1"), Literal("CLAUDE_PLUGINS_STRICT", "1")],
        Resources = InitResources(),
        SecurityContext = RestrictedContext(),
        VolumeMounts =
        [
            new V1VolumeMount { Name = ClaudeHome, MountPath = "/home/node/.claude" },
            new V1VolumeMount { Name = Workspace, MountPath = "/workspace" },
        ],
    };

    private static V1ResourceRequirements InitResources() => new()
    {
        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("256Mi") },
        Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("1Gi") },
    };

    private static V1SecurityContext RestrictedContext() => new()
    {
        AllowPrivilegeEscalation = false,
        ReadOnlyRootFilesystem = false,
        RunAsNonRoot = true,
        Capabilities = new V1Capabilities { Drop = ["ALL"] },
    };

    private V1Container BuildContainer(string podName, TierSettings tier, AgentRepoContext? repo) => new()
    {
        Name = AgentContainerName,
        Image = options.Image,
        Command = ["sh", "-c", StdinLauncher, "claude"],
        Args = BuildArgs(),
        WorkingDir = repo is null ? "/workspace" : RepoDirectory,
        Env = BuildEnvironment(podName, tier, repo is not null),
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
        SecurityContext = RestrictedContext(),
        VolumeMounts =
        [
            new V1VolumeMount { Name = ClaudeHome, MountPath = "/home/node/.claude" },
            new V1VolumeMount { Name = Workspace, MountPath = "/workspace" },
            new V1VolumeMount { Name = McpConfigVolume, MountPath = McpConfigDirectory, ReadOnlyProperty = true },
            new V1VolumeMount { Name = PromptVolume, MountPath = PromptDirectory, ReadOnlyProperty = true },
        ],
    };

    private List<string> BuildArgs() =>
    [
        "-p",
        "--output-format",
        "stream-json",
        "--verbose",
        "--permission-mode",
        "bypassPermissions",
        "--model",
        options.Model,
        "--max-turns",
        options.MaxTurns.ToString(CultureInfo.InvariantCulture),
        "--setting-sources",
        "user,project",
        "--append-system-prompt",
        ResultInstruction,
        "--strict-mcp-config",
        "--mcp-config",
        $"{McpConfigDirectory}/{McpConfigFile}",
    ];

    private List<V1EnvVar> BuildEnvironment(string podName, TierSettings tier, bool hasRepo)
    {
        List<V1EnvVar> variables =
        [
            Literal("HOME", "/home/node"),
            FromSecret("CLAUDE_CODE_OAUTH_TOKEN", CredentialsSecretName, "claude-code-oauth-token"),
            FromSecret(LiteLlmKeyEnv, CredentialsSecretName, tier.LiteLlmKey),
            FromSecret(McpSessionKeyEnv, podName, SessionTokenKey),
            Literal("ANTHROPIC_BASE_URL", options.LiteLlmBaseUrl),
            Literal("ANTHROPIC_CUSTOM_HEADERS", $"x-litellm-api-key: Bearer $({LiteLlmKeyEnv})"),
            Literal("CLAUDE_CODE_ATTRIBUTION_HEADER", "0"),
            Literal("ENABLE_TOOL_SEARCH", "true"),
            Literal("MCP_TIMEOUT", "30000"),
            Literal("DISABLE_AUTOUPDATER", "1"),
            Literal("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1"),
            Literal("SAFE_CHAIN_LOGGING", "silent"),
            Literal("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
            Literal("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
            Literal("OTEL_LOG_TOOL_DETAILS", "1"),
        ];
        if (tier.LogToolContent)
        {
            variables.Add(Literal("OTEL_LOG_TOOL_CONTENT", "1"));
        }

        variables.AddRange(
        [
            Literal("OTEL_LOG_USER_PROMPTS", "1"),
            Literal("OTEL_METRICS_EXPORTER", "otlp"),
            Literal("OTEL_LOGS_EXPORTER", "otlp"),
            Literal("OTEL_TRACES_EXPORTER", "otlp"),
            Literal("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            Literal("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://vmsingle-victoria-metrics-k8s-stack.observability.svc:8428/opentelemetry/v1/metrics"),
            Literal("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "http://victoria-logs-single-server.observability.svc:9428/insert/opentelemetry/v1/logs"),
            Literal("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://victoria-traces-single-vt-single-server.observability.svc:10428/insert/opentelemetry/v1/traces"),
            Literal("OTEL_RESOURCE_ATTRIBUTES", $"agent.namespace={options.Namespace},agent.tier={tier.Label}"),
        ]);
        if (hasRepo)
        {
            variables.Add(FromSecret("GH_TOKEN", podName, GitHubTokenKey));
        }

        return variables;
    }

    private static V1EnvVar Literal(string name, string value) => new() { Name = name, Value = value };

    private static V1EnvVar FromSecret(string name, string secret, string key) => new()
    {
        Name = name,
        ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = secret, Key = key } },
    };

    private sealed record TierSettings(
        string Label,
        string LiteLlmKey,
        string ServiceAccount,
        bool MountServiceAccountToken,
        string PriorityClass,
        string CpuLimit,
        bool LogToolContent)
    {
        public static TierSettings For(AgentTier tier, AgentPodsOptions options) => tier switch
        {
            AgentTier.Read => new TierSettings("read", "litellm-key-read", options.ServiceAccountName, false, "low-priority", "500m", false),
            AgentTier.ClusterRead => new TierSettings(
                "cluster-read",
                "litellm-key-cluster-read",
                options.ClusterReadServiceAccountName
                    ?? throw new AgentConfigurationException("AgentPods:ClusterReadServiceAccountName must be set to run the cluster-read tier."),
                true,
                "high-priority",
                "1250m",
                true),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown agent tier."),
        };
    }
}
