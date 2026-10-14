using AgentPlatform.Host;
using AgentPlatform.Host.Agents;
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
    private static ServiceProvider Build(Dictionary<string, string?> github, Dictionary<string, string?>? readApp = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef",
            ["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform",
            ["LiteLlm:BaseUrl"] = "http://litellm.example:4000",
            ["LiteLlm:ApiKey"] = "test-key",
        };
        foreach (var (key, value) in github)
        {
            settings[$"GitHub:{key}"] = value;
        }

        foreach (var (key, value) in readApp ?? [])
        {
            settings[$"GitHub:ReadApp:{key}"] = value;
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

        Assert.Empty(provider.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value.WebhookSecret);
    }

    [Fact]
    public void TheWorker_RunsTheRenovateTriageWorkflow()
    {
        using var provider = Build(Valid());

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(RenovateTriageWorkflow), options.Workflows.Select(w => w.Type));
        Assert.Contains(typeof(RenovateTriageSweeperWorkflow), options.Workflows.Select(w => w.Type));
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
    [InlineData("Iv1.abc123")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12 3")]
    public void ANonNumericAppId_FailsAtStartup(string appId)
    {
        var github = Valid();
        github["AppId"] = appId;

        var failure = Assert.Throws<InvalidOperationException>(() => Build(github));

        Assert.Contains("GitHub:AppId", failure.Message, StringComparison.Ordinal);
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

    [Fact]
    public void WithoutTheReadAppConfigured_NoReadTokenContributorIsRegistered()
    {
        using var provider = Build(Valid());

        Assert.Empty(provider.GetServices<IRunSecretContributor>());
    }

    [Fact]
    public void WithTheReadAppConfigured_TheWorkerMintsRepoReadTokensForRuns()
    {
        using var provider = Build(Valid(), Valid());

        Assert.IsType<ReadAppSecretContributor>(Assert.Single(provider.GetServices<IRunSecretContributor>()));
    }

    [Theory]
    [InlineData("AppId")]
    [InlineData("PrivateKey")]
    public void AHalfConfiguredReadApp_FailsAtStartup(string missing)
    {
        var readApp = Valid();
        readApp[missing] = null;

        Assert.Throws<InvalidOperationException>(() => Build(Valid(), readApp));
    }

    [Fact]
    public void AReadAppPrivateKeyThatIsNotAPem_FailsAtStartup()
    {
        var readApp = Valid();
        readApp["PrivateKey"] = "not a pem";

        Assert.Throws<InvalidOperationException>(() => Build(Valid(), readApp));
    }

    [Fact]
    public void WithTheReadAppConfigured_TheSharedGitHubClientStillSendsEachHeaderOnce()
    {
        using var provider = Build(Valid(), Valid());

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("github");

        Assert.Equal(["2022-11-28"], client.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version"));
        Assert.Single(client.DefaultRequestHeaders.Accept);
        Assert.Single(client.DefaultRequestHeaders.UserAgent);
    }
}
