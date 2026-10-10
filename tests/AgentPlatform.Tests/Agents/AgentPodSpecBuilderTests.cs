using System.Text.Json;
using AgentPlatform.Host.Agents;
using k8s.Models;

namespace AgentPlatform.Tests.Agents;

public class AgentPodSpecBuilderTests
{
    private static readonly AgentRunRequest Request =
        new("spike-1", "run-1", "claude", "Summarise the repo", AgentTier.Read, 1800);

    private static V1Pod Build(AgentRunRequest? request = null, Action<AgentPodsOptions>? configure = null)
    {
        var options = new AgentPodsOptions();
        configure?.Invoke(options);
        return new AgentPodSpecBuilder(options).Build(request ?? Request);
    }

    private static V1Container Agent(V1Pod pod) => Assert.Single(pod.Spec.Containers);

    private static V1EnvVar Env(V1Pod pod, string name) =>
        Assert.Single(Agent(pod).Env, e => e.Name == name);

    private static string ArgAfter(V1Container container, string flag) =>
        container.Args[container.Args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Pod_UsesTheDeterministicNameInTheAgentNamespace()
    {
        var pod = Build();

        Assert.Equal(AgentPodNaming.PodName("spike-1", "run-1", "claude"), pod.Metadata.Name);
        Assert.Equal("agent-platform-agents", pod.Metadata.NamespaceProperty);
    }

    [Fact]
    public void Pod_CarriesTheManagedByLabelKyvernoKeysOn()
    {
        var pod = Build();

        Assert.Equal("agent-platform", pod.Metadata.Labels["managed-by"]);
        Assert.DoesNotContain(pod.Metadata.Labels.Values, v => v == "n8n-claude-code");
    }

    [Fact]
    public void Pod_RecordsTheOwningRunInAnnotations()
    {
        var pod = Build();

        Assert.Equal("spike-1", pod.Metadata.Annotations["agent-platform/workflow-id"]);
        Assert.Equal("run-1", pod.Metadata.Annotations["agent-platform/run-id"]);
    }

    [Fact]
    public void Pod_RunsToCompletionOnceAsTheAgentRunnerAccount()
    {
        var pod = Build();

        Assert.Equal("Never", pod.Spec.RestartPolicy);
        Assert.Equal("agent-runner", pod.Spec.ServiceAccountName);
        Assert.False(pod.Spec.AutomountServiceAccountToken);
        Assert.Equal("low-priority", pod.Spec.PriorityClassName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1800)]
    [InlineData(3600)]
    public void Pod_SetsTheActiveDeadlineFromTheRequest(int seconds)
    {
        var pod = Build(Request with { DeadlineSeconds = seconds });

        Assert.Equal(seconds, pod.Spec.ActiveDeadlineSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    public void Pod_RejectsDeadlinesOutsideTheKyvernoRange(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Request with { DeadlineSeconds = seconds }));
    }

    [Fact]
    public void Container_UsesThePinnedClaudeCodeImage()
    {
        var image = Agent(Build()).Image;

        Assert.StartsWith("ghcr.io/anthony-spruyt/claude-agent-read:", image, StringComparison.Ordinal);
        Assert.Contains("@sha256:", image, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", image, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_IsPodSecurityRestricted()
    {
        var pod = Build();
        var container = Agent(pod);

        Assert.True(pod.Spec.SecurityContext.RunAsNonRoot);
        Assert.Equal(1000, pod.Spec.SecurityContext.RunAsUser);
        Assert.Equal("RuntimeDefault", pod.Spec.SecurityContext.SeccompProfile.Type);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.Contains("ALL", container.SecurityContext.Capabilities.Drop);
        Assert.Null(container.SecurityContext.Privileged);
    }

    [Fact]
    public void Container_SetsRequestsAndLimits()
    {
        var resources = Agent(Build()).Resources;

        Assert.True(resources.Requests.ContainsKey("cpu"));
        Assert.True(resources.Requests.ContainsKey("memory"));
        Assert.True(resources.Limits.ContainsKey("memory"));
    }

    [Fact]
    public void Container_RoutesModelTrafficThroughLiteLlm()
    {
        var pod = Build();

        Assert.Equal("http://litellm.litellm.svc.cluster.local:4000", Env(pod, "ANTHROPIC_BASE_URL").Value);
    }

    [Fact]
    public void Container_TakesTheReadTierLiteLlmCredentialFromAgentCredentials()
    {
        var reference = Env(Build(), "LITELLM_API_KEY").ValueFrom.SecretKeyRef;

        Assert.Equal("agent-credentials", reference.Name);
        Assert.Equal("litellm-key-read", reference.Key);
    }

    [Fact]
    public void Container_TakesTheClusterReadTierLiteLlmCredentialFromAgentCredentials()
    {
        var pod = Build(Request with { Tier = AgentTier.ClusterRead }, o => o.ClusterReadServiceAccountName = "agent-runner-cluster-read");

        Assert.Equal("litellm-key-cluster-read", Env(pod, "LITELLM_API_KEY").ValueFrom.SecretKeyRef.Key);
        Assert.Equal("agent-runner-cluster-read", pod.Spec.ServiceAccountName);
        Assert.Equal("high-priority", pod.Spec.PriorityClassName);
    }

    [Fact]
    public void Container_RejectsClusterReadWithoutAClusterAccount()
    {
        Assert.Throws<AgentConfigurationException>(() => Build(Request with { Tier = AgentTier.ClusterRead }));
    }

    [Fact]
    public void Container_SendsTheLiteLlmHeaderUsingKubernetesExpansionOfAnEarlierVariable()
    {
        var env = Agent(Build()).Env.ToList();

        var header = Assert.Single(env, e => e.Name == "ANTHROPIC_CUSTOM_HEADERS");
        Assert.Equal("x-litellm-api-key: Bearer $(LITELLM_API_KEY)", header.Value);
        Assert.True(env.FindIndex(e => e.Name == "LITELLM_API_KEY") < env.IndexOf(header));
    }

    [Fact]
    public void Container_TakesTheOauthTokenFromAgentCredentials()
    {
        var reference = Env(Build(), "CLAUDE_CODE_OAUTH_TOKEN").ValueFrom.SecretKeyRef;

        Assert.Equal("agent-credentials", reference.Name);
        Assert.Equal("claude-code-oauth-token", reference.Key);
    }

    [Fact]
    public void Container_NeverCarriesSecretValuesInline()
    {
        var env = Agent(Build()).Env;

        foreach (var name in new[] { "CLAUDE_CODE_OAUTH_TOKEN", "LITELLM_API_KEY", "AGENT_SESSION_TOKEN" })
        {
            var variable = Assert.Single(env, e => e.Name == name);
            Assert.Null(variable.Value);
            Assert.NotNull(variable.ValueFrom.SecretKeyRef);
        }
    }

    [Fact]
    public void Container_TakesTheSessionTokenFromThePerRunSecretNamedAfterThePod()
    {
        var pod = Build();

        var reference = Env(pod, "AGENT_SESSION_TOKEN").ValueFrom.SecretKeyRef;

        Assert.Equal(pod.Metadata.Name, reference.Name);
        Assert.Equal("session-token", reference.Key);
    }

    [Fact]
    public void Container_ExportsTelemetryToTheObservabilityStack()
    {
        var pod = Build();

        Assert.Equal("1", Env(pod, "CLAUDE_CODE_ENABLE_TELEMETRY").Value);
        Assert.Equal("http/protobuf", Env(pod, "OTEL_EXPORTER_OTLP_PROTOCOL").Value);
        Assert.Contains("victoria-traces", Env(pod, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT").Value, StringComparison.Ordinal);
        Assert.Contains("victoria-logs", Env(pod, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT").Value, StringComparison.Ordinal);
        Assert.Contains("vmsingle", Env(pod, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT").Value, StringComparison.Ordinal);
        Assert.Contains("agent.namespace=agent-platform-agents", Env(pod, "OTEL_RESOURCE_ATTRIBUTES").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_KeepsTheN8nEraClaudeCodeTuning()
    {
        var pod = Build();

        Assert.Equal("0", Env(pod, "CLAUDE_CODE_ATTRIBUTION_HEADER").Value);
        Assert.Equal("true", Env(pod, "ENABLE_TOOL_SEARCH").Value);
        Assert.Equal("30000", Env(pod, "MCP_TIMEOUT").Value);
    }

    [Fact]
    public void Container_MountsAClaudeHomeEmptyDirWithASizeLimit()
    {
        var pod = Build();

        var volume = Assert.Single(pod.Spec.Volumes, v => v.Name == "claude-home");
        Assert.NotNull(volume.EmptyDir.SizeLimit);
        var mount = Assert.Single(Agent(pod).VolumeMounts, m => m.Name == "claude-home");
        Assert.Equal("/home/node/.claude", mount.MountPath);
    }

    [Fact]
    public void Container_GetsAWritableWorkspaceWithASizeLimit()
    {
        var pod = Build();

        Assert.Equal("/workspace", Agent(pod).WorkingDir);
        var volume = Assert.Single(pod.Spec.Volumes, v => v.Name == "workspace");
        Assert.NotNull(volume.EmptyDir.SizeLimit);
    }

    [Fact]
    public void Container_RunsClaudeHeadlesslyWithStreamJsonOutput()
    {
        var container = Agent(Build());

        Assert.Equal(["claude"], container.Command);
        Assert.Equal("-p", container.Args[0]);
        Assert.Equal("Summarise the repo", container.Args[1]);
        Assert.Equal("stream-json", ArgAfter(container, "--output-format"));
        Assert.Contains("--verbose", container.Args);
        Assert.Equal("sonnet", ArgAfter(container, "--model"));
    }

    [Fact]
    public void Container_UsesTheConfiguredModel()
    {
        var container = Agent(Build(configure: o => o.Model = "haiku"));

        Assert.Equal("haiku", ArgAfter(container, "--model"));
    }

    [Fact]
    public void Container_EscapesKubernetesVariableReferencesInThePrompt()
    {
        var container = Agent(Build(Request with { Prompt = "echo $(HOME) and $$(x)" }));

        Assert.Equal("echo $$(HOME) and $$$(x)", container.Args[1]);
    }

    [Fact]
    public void Container_PointsTheMcpConfigAtAgentGatewayWithThePerRunToken()
    {
        var config = ArgAfter(Agent(Build()), "--mcp-config");

        using var json = JsonDocument.Parse(config);
        var gateway = json.RootElement.GetProperty("mcpServers").GetProperty("agentplatform");
        Assert.Equal("http", gateway.GetProperty("type").GetString());
        Assert.Equal("http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp", gateway.GetProperty("url").GetString());
        Assert.Equal("Bearer $(AGENT_SESSION_TOKEN)", gateway.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void Container_KeepsTheLiteLlmMcpGatewayForTheTierCredential()
    {
        var config = ArgAfter(Agent(Build()), "--mcp-config");

        using var json = JsonDocument.Parse(config);
        var litellm = json.RootElement.GetProperty("mcpServers").GetProperty("litellm");
        Assert.Equal("http://litellm.litellm.svc.cluster.local:4000/mcp", litellm.GetProperty("url").GetString());
        Assert.Equal("Bearer $(LITELLM_API_KEY)", litellm.GetProperty("headers").GetProperty("x-litellm-api-key").GetString());
    }

    [Fact]
    public void Container_TellsTheAgentToHandBackItsResultThroughTheGateway()
    {
        var prompt = ArgAfter(Agent(Build()), "--append-system-prompt");

        Assert.Contains("mcp__agentplatform__submit_result", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Pod_UsesConfiguredEndpoints()
    {
        var pod = Build(configure: o =>
        {
            o.LiteLlmBaseUrl = "http://llm.test:1";
            o.AgentGatewayUrl = "http://gw.test:2/mcp";
            o.Namespace = "elsewhere";
        });

        Assert.Equal("http://llm.test:1", Env(pod, "ANTHROPIC_BASE_URL").Value);
        Assert.Equal("elsewhere", pod.Metadata.NamespaceProperty);
        Assert.Contains("http://gw.test:2/mcp", ArgAfter(Agent(pod), "--mcp-config"), StringComparison.Ordinal);
    }
}
