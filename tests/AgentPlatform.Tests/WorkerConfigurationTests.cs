using AgentPlatform.Host;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests;

public class WorkerConfigurationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(configuration);
        return services.BuildServiceProvider();
    }

    private static TemporalWorkerServiceOptions PodsWorker(Dictionary<string, string?> settings)
    {
        using var provider = Build(settings);
        return provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPods);
    }

    [Fact]
    public void AgentPodsWorker_CapsConcurrentActivitiesFromConfig()
    {
        var options = PodsWorker(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["AgentPods:MaxConcurrentActivities"] = "3",
        });

        Assert.Equal(3, options.MaxConcurrentActivities);
        Assert.Equal(TaskQueues.AgentPods, options.TaskQueue);
    }

    [Fact]
    public void AgentPodsWorker_DefaultsToTwoConcurrentPods()
    {
        var options = PodsWorker(new() { ["AgentGateway:SessionTokenKey"] = Key });

        Assert.Equal(2, options.MaxConcurrentActivities);
    }

    [Fact]
    public void AgentPodsWorker_UsesTheConfiguredTemporalEndpoint()
    {
        var options = PodsWorker(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["Temporal:Address"] = "temporal.example:7233",
            ["Temporal:Namespace"] = "agents",
        });

        Assert.Equal("temporal.example:7233", options.ClientOptions?.TargetHost);
        Assert.Equal("agents", options.ClientOptions?.Namespace);
    }

    [Fact]
    public void AgentPlatformWorker_RunsTheSpikeWorkflow()
    {
        using var provider = Build(new() { ["AgentGateway:SessionTokenKey"] = Key });

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(SpikeWorkflow), options.Workflows.Select(w => w.Type));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void InvalidConcurrencyCap_FailsAtStartup(string cap)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["AgentPods:MaxConcurrentActivities"] = cap,
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void MissingOrWeakSessionTokenKey_FailsAtStartup(string? key)
    {
        Assert.Throws<ArgumentException>(() => Build(new() { ["AgentGateway:SessionTokenKey"] = key }));
    }

    [Fact]
    public void SchedulingGraceAboveTheActivityTimeoutAllowance_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["AgentPods:SchedulingGraceSeconds"] = "100000",
        }));
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:00:40")]
    [InlineData("00:01:00")]
    public void HeartbeatIntervalNotWellUnderTheTimeout_FailsAtStartup(string interval)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["AgentPods:HeartbeatInterval"] = interval,
        }));
    }

    [Theory]
    [InlineData("00:00:15")]
    [InlineData("00:00:20")]
    [InlineData("00:00:29")]
    public void HeartbeatIntervalWellUnderTheTimeout_Starts(string interval)
    {
        using var provider = Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["AgentPods:HeartbeatInterval"] = interval,
        });

        Assert.NotNull(provider);
    }
}
