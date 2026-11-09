using AgentPlatform.Host;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Tests.Livemap;

public class LivemapConfigurationTests
{
    private static readonly Dictionary<string, string?> Enabled = new()
    {
        ["Livemap:Enabled"] = "true",
        ["Livemap:Repo"] = "octo/livemap",
        ["Livemap:PinRepo"] = "octo/cluster",
        ["Livemap:PinPath"] = "apps/livemap-pin.yaml",
    };

    private static Dictionary<string, string?> WorkerSettings(Dictionary<string, string?>? extra = null)
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
        return settings;
    }

    private static ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(new ConfigurationBuilder().AddInMemoryCollection(WorkerSettings(extra)).Build());
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> With(string key, string? value)
    {
        var settings = new Dictionary<string, string?>(Enabled) { [key] = value };
        return settings;
    }

    [Fact]
    public void WithNoLivemapSettings_TheFeatureIsOff_AndTheWorkerStillStarts()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<LivemapOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), options.PollInterval);
    }

    [Fact]
    public void TheSettings_BindFromConfiguration()
    {
        using var provider = Build(With("Livemap:PollInterval", "00:05:00"));

        var options = provider.GetRequiredService<IOptions<LivemapOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal("octo/livemap", options.Repo);
        Assert.Equal("octo/cluster", options.PinRepo);
        Assert.Equal("apps/livemap-pin.yaml", options.PinPath);
        Assert.Equal(TimeSpan.FromMinutes(5), options.PollInterval);
    }

    [Theory]
    [InlineData("Livemap:Repo", null)]
    [InlineData("Livemap:Repo", "not-a-repo")]
    [InlineData("Livemap:PinRepo", "")]
    [InlineData("Livemap:PinRepo", "a/b/c")]
    [InlineData("Livemap:PinPath", null)]
    [InlineData("Livemap:PinPath", "/absolute/pin.yaml")]
    [InlineData("Livemap:PinPath", "dir/../pin.yaml")]
    [InlineData("Livemap:PinPath", "dir/pin.yaml?ref=other")]
    [InlineData("Livemap:PinPath", "dir/pin.yaml#section")]
    [InlineData("Livemap:PinPath", "dir\\pin.yaml")]
    [InlineData("Livemap:PinPath", "dir//pin.yaml")]
    [InlineData("Livemap:PinPath", "dir/pin file.yaml")]
    [InlineData("Livemap:PinPath", "dir/pin%2Fx.yaml")]
    [InlineData("Livemap:PollInterval", "00:00:30")]
    [InlineData("Livemap:PollInterval", "-00:15:00")]
    public void AnEnabledFeature_WithAnInvalidSetting_FailsAtStartup(string key, string? value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(With(key, value)));
    }

    [Fact]
    public void TheWorker_RunsThePollWorkflowAndItsActivity()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(LivemapPollWorkflow), options.Workflows.Select(w => w.Type));
        Assert.Contains(options.Activities, a => a.Name == LivemapPollActivities.PollName);
    }

    [Fact]
    public void TheWorker_RunsTheBuildWorkflowAndItsActivities()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        Assert.Contains(typeof(LivemapBuildWorkflow), options.Workflows.Select(w => w.Type));
        foreach (var name in new[] { LivemapBuildActivities.InFlightName, LivemapBuildActivities.DispatchName, LivemapBuildActivities.GetRunName, LivemapBuildActivities.ClassifyName, LivemapBuildActivities.ParkName })
        {
            Assert.Contains(options.Activities, a => a.Name == name);
        }
    }

    [Fact]
    public void TheBuildActivities_CanBeBuilt_WithTheirCollaborators()
    {
        using var provider = Build(Enabled);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LivemapBuildActivities>());
    }

    [Fact]
    public void TheWorker_HostsTheScheduleService()
    {
        using var provider = Build();

        Assert.Contains(provider.GetServices<IHostedService>(), s => s is LivemapScheduleService);
    }

    [Fact]
    public void ThePollActivity_CanBeBuilt_WithItsCollaborators()
    {
        using var provider = Build(Enabled);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LivemapPollActivities>());
        Assert.IsType<SteamAppInfoClient>(scope.ServiceProvider.GetRequiredService<ISteamAppInfo>());
        Assert.IsType<ShippedBuildsReader>(scope.ServiceProvider.GetRequiredService<IShippedBuilds>());
        Assert.IsType<TemporalLivemapBuildStarter>(scope.ServiceProvider.GetRequiredService<ILivemapBuildStarter>());
    }

    [Fact]
    public void TheGitHubClient_TalksToTheGitHubApi_AtThePinnedVersion()
    {
        using var provider = Build(Enabled);

        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ILivemapGitHub));

        Assert.Equal("api.github.com", http.BaseAddress?.Host);
        Assert.Equal(["2022-11-28"], http.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version"));
    }

    [Fact]
    public void TheGitHubClient_LeavesRedirectsToItsOwnCode_SoTheTokenStaysOnTheFirstHop()
    {
        using var provider = Build(Enabled);

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(ILivemapGitHub));
        while (handler is DelegatingHandler outer)
        {
            handler = outer.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public void TheGitHubClient_CanBeBuilt_ByTheActivitiesThatNeedIt()
    {
        using var provider = Build(Enabled);
        using var scope = provider.CreateScope();

        Assert.IsType<LivemapGitHub>(scope.ServiceProvider.GetRequiredService<ILivemapGitHub>());
    }

    [Fact]
    public void TheSteamClient_TalksToTheSteamInfoHost_WithABoundedResponse()
    {
        using var provider = Build();

        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ISteamAppInfo));

        Assert.Equal("api.steamcmd.net", http.BaseAddress?.Host);
        Assert.Equal(Uri.UriSchemeHttps, http.BaseAddress?.Scheme);
        Assert.InRange(http.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.InRange(http.MaxResponseContentBufferSize, 1, 16 * 1024 * 1024);
    }

    [Fact]
    public void ThePollActivity_IsScoped_SoTypedHttpClientsRotate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(new ConfigurationBuilder().AddInMemoryCollection(WorkerSettings()).Build());

        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == typeof(LivemapPollActivities)).Lifetime);
    }

    [Fact]
    public void TheShippedBuildsReader_IsTransient_SoItsGitHubClientRotates()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(new ConfigurationBuilder().AddInMemoryCollection(WorkerSettings()).Build());

        Assert.Equal(ServiceLifetime.Transient, services.Last(d => d.ServiceType == typeof(IShippedBuilds)).Lifetime);
    }

    [Fact]
    public void ADisabledFeature_IgnoresItsSettings()
    {
        var settings = With("Livemap:Enabled", "false");
        settings["Livemap:Repo"] = "not-a-repo";

        using var provider = Build(settings);

        Assert.False(provider.GetRequiredService<IOptions<LivemapOptions>>().Value.Enabled);
    }
}
