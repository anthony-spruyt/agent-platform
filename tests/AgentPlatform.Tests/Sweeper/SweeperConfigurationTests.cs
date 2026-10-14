using AgentPlatform.Host;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Sweeper;

public class SweeperConfigurationTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef",
            ["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        GitHubPayloads.AddWorkerSettings(settings);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheSweeperInterval_DefaultsToThirtyMinutes_AndTheOwnerToTheAllowedOwner()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(30), options.Sweeper.Interval);
        Assert.Equal(TriageRules.AllowedOwner, options.OwnerLogin);
    }

    [Fact]
    public void TheSweeperIntervalAndTheOwnerLogin_BindFromConfiguration()
    {
        using var provider = Build(new() { ["RenovateTriage:Sweeper:Interval"] = "00:10:00", ["RenovateTriage:OwnerLogin"] = "someone" });

        var options = provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(10), options.Sweeper.Interval);
        Assert.Equal("someone", options.OwnerLogin);
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:00:00")]
    [InlineData("-00:10:00")]
    public void ASweeperIntervalUnderAMinute_FailsAtStartup(string interval)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["RenovateTriage:Sweeper:Interval"] = interval }));
    }

    [Fact]
    public void ABlankOwnerLogin_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["RenovateTriage:OwnerLogin"] = " " }));
    }

    [Fact]
    public void TheWorker_HostsTheScheduleService_AndTheSignalerAndProbeTheSweeperUses()
    {
        using var provider = Build();

        Assert.Contains(provider.GetServices<IHostedService>(), s => s is SweeperScheduleService);
        Assert.IsType<TemporalRenovateTriageSignaler>(provider.GetRequiredService<IRenovateTriageSignaler>());
        Assert.IsType<TemporalTriageWorkflowProbe>(provider.GetRequiredService<ITriageWorkflowProbe>());
        Assert.NotNull(provider.GetRequiredService<IGitHubAppClient>());
    }
}
