using AgentPlatform.Host;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests;

public class TemporalSettingsBindingTests
{
    private static TemporalWorkerServiceOptions ResolveWorkerOptions(Dictionary<string, string?> settings)
    {
        settings["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef";
        settings["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform";
        GitHubPayloads.AddWorkerSettings(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);
    }

    [Fact]
    public void Configured_TemporalSettings_FlowIntoWorkerOptions()
    {
        var options = ResolveWorkerOptions(new()
        {
            ["Temporal:Address"] = "temporal.example:7233",
            ["Temporal:Namespace"] = "agents",
        });

        Assert.Equal("temporal.example:7233", options.ClientOptions?.TargetHost);
        Assert.Equal("agents", options.ClientOptions?.Namespace);
        Assert.Equal(TaskQueues.AgentPlatform, options.TaskQueue);
    }

    [Fact]
    public void Unset_TemporalSettings_UseDefaults()
    {
        var options = ResolveWorkerOptions([]);

        Assert.Equal("localhost:7233", options.ClientOptions?.TargetHost);
        Assert.Equal("default", options.ClientOptions?.Namespace);
        Assert.Equal(TaskQueues.AgentPlatform, options.TaskQueue);
    }
}
