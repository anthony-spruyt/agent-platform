using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Host;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddAgentPlatformWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var temporal = configuration.GetSection(TemporalOptions.SectionName).Get<TemporalOptions>() ?? new TemporalOptions();
        var pods = configuration.GetSection(AgentPodsOptions.SectionName).Get<AgentPodsOptions>() ?? new AgentPodsOptions();
        var gateway = configuration.GetSection(AgentGatewayOptions.SectionName).Get<AgentGatewayOptions>() ?? new AgentGatewayOptions();
        var prompts = configuration.GetSection(AgentPromptsOptions.SectionName).Get<AgentPromptsOptions>() ?? new AgentPromptsOptions();
        Validate(pods);
        Validate(prompts);

        services
            .AddHostedTemporalWorker(temporal.Address, temporal.Namespace, TaskQueues.AgentPlatform)
            .AddWorkflow<PingWorkflow>()
            .AddWorkflow<SpikeWorkflow>()
            .AddWorkflow<RenovateTriageWorkflow>()
            .AddSingletonActivities<PingActivities>()
            .AddSingletonActivities<PromptActivities>()
            .AddSingletonActivities<RunDataActivities>()
            .AddSingletonActivities<ClaudeBudgetActivities>()
            .AddScopedActivities<RenovateTriageActivities>()
            .AddScopedActivities<RenovateTriageCodeActivities>()
            .AddScopedActivities<ClaudeRungActivities>()
            .AddScopedActivities<TriageCheckActivities>()
            .AddScopedActivities<AgentPodActivities>();

        services
            .AddHostedTemporalWorker(temporal.Address, temporal.Namespace, TaskQueues.AgentPods)
            .ConfigureOptions(options => options.MaxConcurrentActivities = pods.MaxConcurrentActivities)
            .AddScopedActivities<AgentPodActivities>();

        services.AddRunData(configuration);
        services.AddGitHubAppClient(configuration);
        services.AddPullRequests(configuration);
        services.AddGitHubReadApp(configuration);
        services.AddRenovateTriage(configuration);
        services.AddSingleton(pods);
        services.AddSingleton(prompts);
        services.AddSingleton(new AgentSessionTokens(gateway.SessionTokenKey));
        services.AddSingleton<AgentPodSpecBuilder>();
        services.AddSingleton<IKubernetes>(_ => new Kubernetes(
            KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildDefaultConfig()));
        services.AddSingleton<IAgentRunner, KubernetesAgentRunner>();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IExitCodeSetter, EnvironmentExitCodeSetter>();
        services.AddHostedService<TemporalWorkerFaultMonitor>();
        services.AddSingleton<TriageGauges>();
        services.AddHostedService<TriageGaugeRefresher>();

        return services;
    }

    private static void Validate(AgentPromptsOptions prompts)
    {
        if (prompts.Directory is { Length: > 0 } directory && !Path.IsPathFullyQualified(directory))
        {
            throw new InvalidOperationException("AgentPrompts:Directory must be an absolute path.");
        }
    }

    private static void Validate(AgentPodsOptions pods)
    {
        if (pods.MaxConcurrentActivities < 1)
        {
            throw new InvalidOperationException("AgentPods:MaxConcurrentActivities must be at least 1.");
        }

        if (pods.MaxTurns < 1)
        {
            throw new InvalidOperationException("AgentPods:MaxTurns must be at least 1.");
        }

        if (pods.MaxBudgetUsd <= 0)
        {
            throw new InvalidOperationException("AgentPods:MaxBudgetUsd must be greater than 0.");
        }

        if (pods.SchedulingGraceSeconds is < 0 or > AgentRunTimeouts.SchedulingGraceSeconds)
        {
            throw new InvalidOperationException($"AgentPods:SchedulingGraceSeconds must be between 0 and {AgentRunTimeouts.SchedulingGraceSeconds}.");
        }

        if (pods.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("AgentPods:HeartbeatInterval must be positive.");
        }

        if (pods.HeartbeatInterval * 2 >= AgentRunTimeouts.HeartbeatTimeout)
        {
            throw new InvalidOperationException($"AgentPods:HeartbeatInterval must be less than half of the {AgentRunTimeouts.HeartbeatTimeout.TotalSeconds:0}s heartbeat timeout.");
        }
    }
}
