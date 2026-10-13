using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.PullRequests;

public class PullRequestsRegistrationTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTokens : IGitHubInstallationTokenProvider
    {
        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken) =>
            Task.FromResult("ghs_install_token");
    }

    [Fact]
    public async Task TheRegisteredGitHubClient_DropsTheTriageRunsOfTheConfiguredApp_AndTheReporterIsResolvable()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""
            {"total_count":2,"check_runs":[
              {"id":1,"name":"Agent triage","status":"in_progress","external_id":"renovate-triage/o/r/7@abc","app":{"id":12345,"slug":"skynet-rw"}},
              {"id":2,"name":"build","status":"completed","conclusion":"success","app":{"id":15368,"slug":"github-actions"}}]}
            """));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:AppId"] = "12345", ["GitHub:PrivateKey"] = GitHubPayloads.PrivateKeyPem })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubAppClient(configuration);
        services.AddPullRequests(configuration);
        services.AddSingleton<IGitHubInstallationTokenProvider>(new FixedTokens());
        services.AddHttpClient(nameof(IGitHubApiClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var runs = await provider.GetRequiredService<IGitHubApiClient>().ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal([2L], runs.Select(r => r.Id));
        Assert.IsType<CheckRunReporter>(provider.GetRequiredService<ICheckRunReporter>());
        Assert.Equal("12345", provider.GetRequiredService<IOptions<GitHubAppOptions>>().Value.AppId);
    }

    [Fact]
    public async Task TheScannerClientFromTheRealServiceCollection_WaitsForASlowScanAndSendsItOnce()
    {
        var attempts = 0;
        var slow = new SlowHandler(async ct =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
            return StubHttpHandler.Json("""{"flagged":[],"unknown":[]}""");
        });
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(null);
        builder.AddServiceDefaults();
        builder.Services.AddPullRequests(builder.Configuration);
        builder.Services.AddHttpClient(UntrustedTextScannerClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => slow);
        await using var provider = builder.Services.BuildServiceProvider();

        var scan = await provider.GetRequiredService<IUntrustedTextScanner>().ScanAsync("changelog", Ct);

        Assert.Equal(ScanOutcome.Clean, scan.Outcome);
        Assert.Equal(1, attempts);
    }

    private sealed class SlowHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }
}
