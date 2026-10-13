using System.Text.Json;
using AgentPlatform.Host.Agents;
using k8s.Models;

namespace AgentPlatform.Tests.Agents;

public class AgentPodSpecBuilderTests
{
    private static readonly AgentRunRequest Request =
        new("spike-1", "run-1", "claude", "Summarise the repo", AgentTier.Read, 1800);

    private static readonly AgentRepoContext Repo = new("octo/widgets", 7, "0123456789abcdef0123456789abcdef01234567");

    private static AgentRunRequest RepoRequest => Request with { Repo = Repo };

    private static V1Pod Build(AgentRunRequest? request = null, Action<AgentPodsOptions>? configure = null)
    {
        var options = new AgentPodsOptions();
        configure?.Invoke(options);
        return new AgentPodSpecBuilder(options).Build(request ?? Request);
    }

    private static JsonElement McpServers(Action<AgentPodsOptions>? configure = null)
    {
        var options = new AgentPodsOptions();
        configure?.Invoke(options);
        return JsonDocument.Parse(new AgentPodSpecBuilder(options).BuildMcpConfig()).RootElement.GetProperty("mcpServers").Clone();
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
        var pod = Build(RepoRequest);

        Assert.True(pod.Spec.SecurityContext.RunAsNonRoot);
        Assert.Equal(1000, pod.Spec.SecurityContext.RunAsUser);
        Assert.Equal("RuntimeDefault", pod.Spec.SecurityContext.SeccompProfile.Type);
        foreach (var container in pod.Spec.InitContainers.Concat(pod.Spec.Containers))
        {
            Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
            Assert.Contains("ALL", container.SecurityContext.Capabilities.Drop);
            Assert.Null(container.SecurityContext.Privileged);
        }
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
        var reference = Env(Build(), "AP_LITELLM_API_KEY").ValueFrom.SecretKeyRef;

        Assert.Equal("agent-credentials", reference.Name);
        Assert.Equal("litellm-key-read", reference.Key);
    }

    [Fact]
    public void Container_TakesTheClusterReadTierLiteLlmCredentialFromAgentCredentials()
    {
        var pod = Build(Request with { Tier = AgentTier.ClusterRead }, o => o.ClusterReadServiceAccountName = "agent-runner-cluster-read");

        Assert.Equal("litellm-key-cluster-read", Env(pod, "AP_LITELLM_API_KEY").ValueFrom.SecretKeyRef.Key);
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
        Assert.Equal("x-litellm-api-key: Bearer $(AP_LITELLM_API_KEY)", header.Value);
        Assert.True(env.FindIndex(e => e.Name == "AP_LITELLM_API_KEY") < env.IndexOf(header));
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
        var env = Agent(Build(RepoRequest)).Env;

        foreach (var name in new[] { "CLAUDE_CODE_OAUTH_TOKEN", "AP_LITELLM_API_KEY", "AP_MCP_SESSION_KEY", "GH_TOKEN" })
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

        var reference = Env(pod, "AP_MCP_SESSION_KEY").ValueFrom.SecretKeyRef;

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

        Assert.Equal("-p", container.Args[0]);
        Assert.Equal("stream-json", ArgAfter(container, "--output-format"));
        Assert.Contains("--verbose", container.Args);
        Assert.Equal("sonnet", ArgAfter(container, "--model"));
    }

    [Fact]
    public void Container_ReadsThePromptFromStdinSoItNeverAppearsInArgs()
    {
        var container = Agent(Build(Request with { Prompt = "echo $(HOME) and $$" }));

        Assert.Equal(["sh", "-c", "exec claude \"$@\" < /run/ap/prompt/prompt.txt", "claude"], container.Command);
        Assert.DoesNotContain(container.Args, a => a.Contains("echo", StringComparison.Ordinal));
        Assert.DoesNotContain(container.Args, a => a.Contains("$(", StringComparison.Ordinal));
        Assert.DoesNotContain(container.Command, c => c.Contains("$(", StringComparison.Ordinal));
    }

    [Fact]
    public void Container_UsesTheConfiguredModel()
    {
        var container = Agent(Build(configure: o => o.Model = "haiku"));

        Assert.Equal("haiku", ArgAfter(container, "--model"));
    }

    [Fact]
    public void Container_CapsTurnsAndLoadsOnlyUserAndProjectSettings()
    {
        var container = Agent(Build(configure: o => o.MaxTurns = 12));

        Assert.Equal("12", ArgAfter(container, "--max-turns"));
        Assert.Equal("user,project", ArgAfter(container, "--setting-sources"));
        Assert.DoesNotContain("--add-dir", container.Args);
    }

    [Fact]
    public void Container_CapsSpendInvariantly()
    {
        var container = Agent(Build(configure: o => o.MaxBudgetUsd = 1.25m));

        Assert.Equal("1.25", ArgAfter(container, "--max-budget-usd"));
        Assert.Equal("2", ArgAfter(Agent(Build()), "--max-budget-usd"));
    }

    [Fact]
    public void Container_UsesOnlyTheMcpConfigFileFromTheSecret()
    {
        var pod = Build();
        var container = Agent(pod);

        Assert.Contains("--strict-mcp-config", container.Args);
        Assert.Equal("/run/ap/mcp/mcp.json", ArgAfter(container, "--mcp-config"));
        var volume = Assert.Single(pod.Spec.Volumes, v => v.Name == "mcp-config");
        Assert.Equal(pod.Metadata.Name, volume.Secret.SecretName);
        var item = Assert.Single(volume.Secret.Items);
        Assert.Equal("mcp-config.json", item.Key);
        Assert.Equal("mcp.json", item.Path);
    }

    [Fact]
    public void Container_MountsEverySecretVolumeThroughASingleKeyItem()
    {
        var pod = Build(RepoRequest);

        foreach (var volume in pod.Spec.Volumes.Where(v => v.Secret is not null))
        {
            Assert.Single(volume.Secret.Items);
        }

        Assert.DoesNotContain(Agent(pod).VolumeMounts, m => m.Name == "git-credentials");
    }

    [Fact]
    public void McpConfig_ReferencesTheKeyVariablesByNameAndCarriesNoValues()
    {
        var servers = McpServers();

        var gateway = servers.GetProperty("agentplatform");
        Assert.Equal("http", gateway.GetProperty("type").GetString());
        Assert.Equal("http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp", gateway.GetProperty("url").GetString());
        Assert.Equal("Bearer ${AP_MCP_SESSION_KEY}", gateway.GetProperty("headers").GetProperty("Authorization").GetString());
        var litellm = servers.GetProperty("litellm");
        Assert.Equal("http://litellm.litellm.svc.cluster.local:4000/mcp", litellm.GetProperty("url").GetString());
        Assert.Equal("Bearer ${AP_LITELLM_API_KEY}", litellm.GetProperty("headers").GetProperty("x-litellm-api-key").GetString());
    }

    [Fact]
    public void McpConfig_InHelperModeDelegatesTheHeadersToTheImageHelper()
    {
        var servers = McpServers(o => o.UseMcpHeadersHelper = true);

        foreach (var name in new[] { "agentplatform", "litellm" })
        {
            var server = servers.GetProperty(name);
            Assert.Equal("/usr/local/bin/ap-mcp-headers", server.GetProperty("headersHelper").GetString());
            Assert.False(server.TryGetProperty("headers", out _));
        }
    }

    [Fact]
    public void McpConfig_UsesConfiguredEndpoints()
    {
        var servers = McpServers(o =>
        {
            o.LiteLlmBaseUrl = "http://llm.test:1/";
            o.AgentGatewayUrl = "http://gw.test:2/mcp";
        });

        Assert.Equal("http://gw.test:2/mcp", servers.GetProperty("agentplatform").GetProperty("url").GetString());
        Assert.Equal("http://llm.test:1/mcp", servers.GetProperty("litellm").GetProperty("url").GetString());
    }

    [Fact]
    public void StaticSecretData_HoldsThePromptAndTheMcpConfig()
    {
        var data = new AgentPodSpecBuilder(new AgentPodsOptions()).BuildStaticSecretData(Request);

        Assert.Equal("Summarise the repo", data["prompt.txt"]);
        Assert.Contains("${AP_MCP_SESSION_KEY}", data["mcp-config.json"], StringComparison.Ordinal);
    }

    [Fact]
    public void Container_TellsTheAgentToHandBackItsResultThroughTheGateway()
    {
        var prompt = ArgAfter(Agent(Build()), "--append-system-prompt");

        Assert.Contains("mcp__agentplatform__submit_result", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Pod_UsesConfiguredEndpointsAndNamespace()
    {
        var pod = Build(configure: o =>
        {
            o.LiteLlmBaseUrl = "http://llm.test:1";
            o.Namespace = "elsewhere";
        });

        Assert.Equal("http://llm.test:1", Env(pod, "ANTHROPIC_BASE_URL").Value);
        Assert.Equal("elsewhere", pod.Metadata.NamespaceProperty);
    }

    [Fact]
    public void Pod_WithoutARepoHasNoInitContainersAndNoGitHubToken()
    {
        var pod = Build();

        Assert.Null(pod.Spec.InitContainers);
        Assert.DoesNotContain(Agent(pod).Env, e => e.Name == "GH_TOKEN");
        Assert.DoesNotContain(pod.Spec.Volumes, v => v.Name == "git-credentials");
        Assert.Equal("/workspace", Agent(pod).WorkingDir);
    }

    [Fact]
    public void Pod_WithARepoClonesThenBootstrapsPluginsBeforeTheAgent()
    {
        var pod = Build(RepoRequest);

        Assert.Equal(["git-clone", "plugins"], pod.Spec.InitContainers.Select(c => c.Name));
        Assert.Equal("/workspace/repo", Agent(pod).WorkingDir);
        var plugins = pod.Spec.InitContainers[1];
        Assert.Equal(["/usr/local/bin/claude-plugins-bootstrap", "/workspace/repo"], plugins.Command);
        Assert.Contains(plugins.VolumeMounts, m => m.Name == "claude-home" && m.MountPath == "/home/node/.claude");
        Assert.Equal("1", Assert.Single(plugins.Env, e => e.Name == "CLAUDE_PLUGINS_STRICT").Value);
    }

    [Fact]
    public void CloneInit_TakesTheDefaultBranchShallowAndFetchesThePullRequestHeadBySha()
    {
        var clone = Assert.Single(Build(RepoRequest).Spec.InitContainers, c => c.Name == "git-clone");
        var script = clone.Command[2];

        Assert.Equal("sh", clone.Command[0]);
        Assert.Contains("clone --quiet --depth 1", script, StringComparison.Ordinal);
        Assert.Contains("fetch --quiet --depth 1", script, StringComparison.Ordinal);
        Assert.Contains("/workspace/repo", script, StringComparison.Ordinal);
        Assert.Contains("/workspace/pr", script, StringComparison.Ordinal);
        Assert.Equal("octo/widgets", clone.Command[4]);
        Assert.Equal(Repo.HeadSha, clone.Command[5]);
        Assert.DoesNotContain("--branch", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CloneInit_AloneMountsTheGitCredential()
    {
        var pod = Build(RepoRequest);

        var mounted = pod.Spec.InitContainers.Concat(pod.Spec.Containers)
            .Where(c => c.VolumeMounts.Any(m => m.Name == "git-credentials"))
            .Select(c => c.Name);
        Assert.Equal(["git-clone"], mounted);
        var volume = Assert.Single(pod.Spec.Volumes, v => v.Name == "git-credentials");
        Assert.Equal("git-credentials", Assert.Single(volume.Secret.Items).Key);
    }

    [Fact]
    public void Pod_WithARepoPassesTheGitHubTokenAsEnvFromThePerRunSecret()
    {
        var pod = Build(RepoRequest);

        var reference = Env(pod, "GH_TOKEN").ValueFrom.SecretKeyRef;
        Assert.Equal(pod.Metadata.Name, reference.Name);
        Assert.Equal("github-token", reference.Key);
    }

    [Fact]
    public void Pod_NeverExpandsAVariableInACommandOrArgument()
    {
        var pod = Build(RepoRequest);

        var all = pod.Spec.InitContainers.Concat(pod.Spec.Containers).SelectMany(c => (c.Command ?? []).Concat(c.Args ?? []));
        Assert.DoesNotContain(all, a => a.Contains("$(", StringComparison.Ordinal));
        Assert.DoesNotContain(all, a => a.Contains("x-access-token", StringComparison.Ordinal));
    }

    [Fact]
    public void Pod_DoesNotLogToolContentOnTheReadTier()
    {
        Assert.DoesNotContain(Agent(Build(RepoRequest)).Env, e => e.Name == "OTEL_LOG_TOOL_CONTENT");
    }

    [Fact]
    public void Pod_KeepsToolContentLoggingOnTheClusterReadTier()
    {
        var pod = Build(Request with { Tier = AgentTier.ClusterRead }, o => o.ClusterReadServiceAccountName = "agent-runner-cluster-read");

        Assert.Equal("1", Env(pod, "OTEL_LOG_TOOL_CONTENT").Value);
    }

    [Fact]
    public void Pod_RejectsAMalformedRepoContext()
    {
        Assert.ThrowsAny<ArgumentException>(() => Build(Request with { Repo = Repo with { Repo = "not a repo" } }));
        Assert.ThrowsAny<ArgumentException>(() => Build(Request with { Repo = Repo with { PullRequest = 0 } }));
        Assert.ThrowsAny<ArgumentException>(() => Build(Request with { Repo = Repo with { HeadSha = "main" } }));
        Assert.ThrowsAny<ArgumentException>(() => Build(Request with { Repo = Repo with { HeadSha = new string('z', 40) } }));
    }
}
