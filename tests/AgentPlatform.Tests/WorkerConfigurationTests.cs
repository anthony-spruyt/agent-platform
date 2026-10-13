using AgentPlatform.Host;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests;

public class WorkerConfigurationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private const string Connection = "Host=localhost;Database=agent_platform";

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings.TryAdd("ConnectionStrings:rundata", Connection);
        GitHubPayloads.AddWorkerSettings(settings);
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

    [Fact]
    public void AgentPlatformWorker_RegistersTheRunDataStore()
    {
        using var provider = Build(new() { ["AgentGateway:SessionTokenKey"] = Key });

        Assert.NotNull(provider.GetService<RunDataStore>());
        Assert.NotNull(provider.GetService<IDbContextFactory<RunDataDbContext>>());
    }

    [Fact]
    public void AgentPlatformWorker_UsesTheRunDataConnectionString()
    {
        using var provider = Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["ConnectionStrings:rundata"] = "Host=db.example;Database=agent_platform",
        });

        using var db = provider.GetRequiredService<IDbContextFactory<RunDataDbContext>>().CreateDbContext();

        Assert.Contains("db.example", db.Database.GetConnectionString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingRunDataConnectionString_FailsAtStartup(string? connection)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["ConnectionStrings:rundata"] = connection,
        }));
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
    [InlineData("MaxTurns", "0")]
    [InlineData("MaxTurns", "-1")]
    [InlineData("MaxTokens", "0")]
    [InlineData("MaxSubagents", "0")]
    [InlineData("MaxSubagents", "-3")]
    public void InvalidRunLimit_FailsAtStartup(string option, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            [$"AgentPods:{option}"] = value,
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

    [Fact]
    public void AgentPlatformWorker_RegistersTheRenovateTriageServices()
    {
        using var provider = Build(new() { ["AgentGateway:SessionTokenKey"] = Key });

        Assert.NotNull(provider.GetService<IJevClient>());
        Assert.NotNull(provider.GetService<TriageVerdictStore>());
        Assert.NotNull(provider.GetService<RenovateTriageActivities>());
        Assert.Equal(TriageMode.Shadow, provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value.Mode);
    }

    [Fact]
    public void AgentPlatformWorker_RunsTheRenovateTriageActivities()
    {
        using var provider = Build(new() { ["AgentGateway:SessionTokenKey"] = Key });

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(RenovateTriageWorkflow), options.Workflows.Select(w => w.Type));
        Assert.Contains(options.Activities, a => a.Name == RenovateTriageActivities.AssessName);
    }

    [Fact]
    public void TheRenovateTriageActivities_AreScoped_SoTypedHttpClientsRotate()
    {
        var settings = new Dictionary<string, string?>
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["ConnectionStrings:rundata"] = Connection,
        };
        GitHubPayloads.AddWorkerSettings(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(configuration);

        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == typeof(RenovateTriageActivities)).Lifetime);
    }

    [Fact]
    public void TheLiteLlmBaseUrl_DefaultsToTheInClusterService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LiteLlm:ApiKey"] = "test-key" }).Build();
        var services = new ServiceCollection();
        services.AddRenovateTriage(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("http://litellm.litellm.svc.cluster.local:4000", provider.GetRequiredService<IOptions<LiteLlmOptions>>().Value.BaseUrl);
    }

    [Theory]
    [InlineData("LiteLlm:BaseUrl", "litellm.example")]
    [InlineData("LiteLlm:ApiKey", null)]
    [InlineData("LiteLlm:ApiKey", "  ")]
    public void MissingOrInvalidLiteLlmSettings_FailAtStartup(string name, string? value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            [name] = value,
        }));
    }

    [Fact]
    public void LiveTriageMode_IsAccepted()
    {
        using var provider = Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["RenovateTriage:Mode"] = "Live",
        });

        Assert.Equal(TriageMode.Live, provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value.Mode);
    }

    [Fact]
    public void AnUnknownTriageMode_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["AgentGateway:SessionTokenKey"] = Key,
            ["RenovateTriage:Mode"] = "Active",
        }));
    }
}
