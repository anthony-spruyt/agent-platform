using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapWebRoleTests
{
    private static WebApplicationFactory<Program> Factory(bool enabled, string repo = "octo/livemap") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", new string('k', 32));
            builder.UseSetting("GitHub:WebhookSecret", GitHubPayloads.Secret);
            builder.UseSetting("GitHub:AppId", "123");
            builder.UseSetting("Livemap:Enabled", enabled ? "true" : "false");
            builder.UseSetting("Livemap:Repo", repo);
            builder.UseSetting("Livemap:PinRepo", "octo/cluster");
            builder.UseSetting("Livemap:PinPath", "apps/livemap-pin.yaml");
            builder.UseSetting("Livemap:TrackingIssue", "12");
            builder.UseSetting("Livemap:RequiredChecks:0", "lint / MegaLinter");
            builder.UseSetting("Livemap:ReleaseBot", "release-bot[bot]");
            builder.UseSetting("Livemap:ReleaseRequiredChecks:0", "lint / MegaLinter");
        });

    [Fact]
    public void TheWebRole_WakesTheBuildThroughTheTemporalClient()
    {
        using var factory = Factory(enabled: true);

        using var scope = factory.Services.CreateScope();

        Assert.IsType<TemporalLivemapWakeStarter>(scope.ServiceProvider.GetRequiredService<ILivemapWakeStarter>());
        Assert.Contains(scope.ServiceProvider.GetServices<IGitHubEventHandler>(), handler => handler is LivemapCheckSuiteHandler);
        Assert.Equal("octo/livemap", scope.ServiceProvider.GetRequiredService<IOptions<LivemapOptions>>().Value.Repo);
    }

    [Fact]
    public void TheWebRole_HasNoPartOfTheWorkerSide()
    {
        using var factory = Factory(enabled: true);

        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<LivemapBuildStore>());
        Assert.Null(scope.ServiceProvider.GetService<ILivemapBuildStarter>());
        Assert.Null(scope.ServiceProvider.GetService<ILivemapGitHub>());
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IHostedService>(), service => service is LivemapScheduleService);
    }

    [Fact]
    public void TheWebRole_StartsWithTheFeatureOff_AndWakesNothing()
    {
        using var factory = Factory(enabled: false, repo: string.Empty);

        using var scope = factory.Services.CreateScope();

        Assert.False(scope.ServiceProvider.GetRequiredService<IOptions<LivemapOptions>>().Value.Enabled);
    }

    [Fact]
    public void TheWebRole_FailsAtStartup_WhenTheFeatureIsOnWithoutAForkRepo()
    {
        using var factory = Factory(enabled: true, repo: "not-a-repo");

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Livemap:Repo", failure.Message, StringComparison.Ordinal);
    }
}
