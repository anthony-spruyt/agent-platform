using AgentPlatform.Host;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests.GitHub;

public class WorkerGitHubTests
{
    private static ServiceProvider Build(Dictionary<string, string?> github)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef",
            ["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform",
        };
        foreach (var (key, value) in github)
        {
            settings[$"GitHub:{key}"] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Valid() => new() { ["AppId"] = "12345", ["PrivateKey"] = GitHubPayloads.PrivateKeyPem };

    [Fact]
    public void TheWorker_ProvidesTheGitHubApiClientAndTheConfiguredAppOptions()
    {
        using var provider = Build(Valid());

        Assert.IsType<GitHubApiClient>(provider.GetRequiredService<IGitHubApiClient>());
        Assert.Equal("12345", provider.GetRequiredService<IOptions<GitHubAppOptions>>().Value.AppId);
    }

    [Fact]
    public void TheWorker_DoesNotHandleWebhooks()
    {
        using var provider = Build(Valid());

        Assert.Null(provider.GetService<IRenovateTriageSignaler>());
        Assert.Empty(provider.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value.WebhookSecret);
    }

    [Fact]
    public void TheWorker_RunsTheRenovateTriageWorkflow()
    {
        using var provider = Build(Valid());

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(RenovateTriageWorkflow), options.Workflows.Select(w => w.Type));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingAppId_FailsAtStartup(string? appId)
    {
        var github = Valid();
        github["AppId"] = appId;

        Assert.Throws<InvalidOperationException>(() => Build(github));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingPrivateKey_FailsAtStartup(string? key)
    {
        var github = Valid();
        github["PrivateKey"] = key;

        Assert.Throws<InvalidOperationException>(() => Build(github));
    }

    [Fact]
    public void APrivateKeyThatIsNotAPem_FailsAtStartup()
    {
        var github = Valid();
        github["PrivateKey"] = "not a pem";

        Assert.Throws<InvalidOperationException>(() => Build(github));
    }
}
