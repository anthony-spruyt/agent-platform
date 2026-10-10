using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Host;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddAgentPlatformWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var temporal = configuration.GetSection(TemporalOptions.SectionName).Get<TemporalOptions>() ?? new TemporalOptions();

        services
            .AddHostedTemporalWorker(temporal.Address, temporal.Namespace, TaskQueues.AgentPlatform)
            .AddWorkflow<PingWorkflow>()
            .AddSingletonActivities<PingActivities>();

        return services;
    }
}
