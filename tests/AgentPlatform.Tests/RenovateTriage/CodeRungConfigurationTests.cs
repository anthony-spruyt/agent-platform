using AgentPlatform.Host;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests.RenovateTriage;

public class CodeRungConfigurationTests
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
    public void TheWorkerRunsTheCodeRungActivities()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(options.Activities, a => a.Name == RenovateTriageCodeActivities.FetchChangelogName);
        Assert.Contains(options.Activities, a => a.Name == RenovateTriageCodeActivities.DetectMigrationName);
        Assert.Contains(options.Activities, a => a.Name == RenovateTriageCodeActivities.ClassifyCiName);
        Assert.Contains(options.Activities, a => a.Name == RenovateTriageCodeActivities.RerunFailedJobsName);
        Assert.NotNull(provider.GetService<RenovateTriageCodeActivities>());
    }

    [Fact]
    public void TheWorkerReportsTheTriageCheckThroughTheCheckRunReporter()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(options.Activities, a => a.Name == TriageCheckActivities.ReportName);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<TriageCheckActivities>());
        Assert.NotNull(scope.ServiceProvider.GetService<IUntrustedTextScanner>());
    }

    [Fact]
    public void TheMigrationGlobsDefaultToTheTalosPathsAndPackages()
    {
        using var provider = Build();

        var migration = new MigrationDetector(provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value);

        Assert.NotNull(migration.Detect(["talos/a.yaml"], []));
        Assert.NotNull(migration.Detect([], ["kubelet"]));
    }

    [Fact]
    public void ConfiguredMigrationGlobsReplaceTheDefaultsInsteadOfAddingToThem()
    {
        using var provider = Build(new()
        {
            ["RenovateTriage:Migration:PathGlobs:0"] = "infra/**",
            ["RenovateTriage:Migration:DependencyGlobs:0"] = "acme/*",
        });

        var migration = new MigrationDetector(provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value);

        Assert.NotNull(migration.Detect(["infra/main.tf"], []));
        Assert.NotNull(migration.Detect([], ["acme/db"]));
        Assert.Null(migration.Detect(["talos/a.yaml"], []));
        Assert.Null(migration.Detect([], ["kubelet"]));
    }
}
