using AgentPlatform.Host;
using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapHardPathConfigurationTests
{
    private static readonly Dictionary<string, string?> Enabled = new()
    {
        ["Livemap:Enabled"] = "true",
        ["Livemap:Repo"] = "octo/livemap",
        ["Livemap:PinRepo"] = "octo/cluster",
        ["Livemap:PinPath"] = "apps/livemap-pin.yaml",
        ["Livemap:TrackingIssue"] = "12",
        ["Livemap:RequiredChecks:0"] = "lint / MegaLinter",
        ["Livemap:ReleaseBot"] = "release-bot[bot]",
        ["Livemap:ReleaseRequiredChecks:0"] = "lint / MegaLinter",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var all = new Dictionary<string, string?>(settings)
        {
            ["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef",
            ["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform",
        };
        GitHubPayloads.AddWorkerSettings(all);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPlatformWorker(new ConfigurationBuilder().AddInMemoryCollection(all).Build());
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> With(string key, string? value, bool hardPath = true) =>
        new(Enabled) { ["Livemap:HardPath"] = hardPath ? "true" : "false", [key] = value };

    [Fact]
    public void TheHardPath_IsOffUnlessTheSettingTurnsItOn()
    {
        using var off = Build(Enabled);
        using var on = Build(With("Livemap:Enabled", "true"));

        Assert.False(off.GetRequiredService<IOptions<LivemapOptions>>().Value.HardPath);
        Assert.True(on.GetRequiredService<IOptions<LivemapOptions>>().Value.HardPath);
    }

    [Fact]
    public void TheAgentSettings_AllHaveWorkingDefaults()
    {
        using var provider = Build(With("Livemap:Enabled", "true"));

        var options = provider.GetRequiredService<IOptions<LivemapOptions>>().Value;

        Assert.Equal(3, options.AgentRunsPerBuild);
        Assert.Equal(2700, options.AgentDeadlineSeconds);
        Assert.False(string.IsNullOrWhiteSpace(options.AgentModel));
        Assert.True(options.AgentMaxTurns > 40);
        Assert.True(options.AgentMaxBudgetUsd > 2m);
        Assert.StartsWith("https://", options.DepotDownloaderUrl, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", options.DepotDownloaderSha256);
        Assert.True(Uri.IsWellFormedUriString(options.PythonIndexUrl, UriKind.Absolute));
    }

    [Theory]
    [InlineData("Livemap:AgentModel", "")]
    [InlineData("Livemap:AgentModel", " ")]
    [InlineData("Livemap:AgentMaxTurns", "0")]
    [InlineData("Livemap:AgentMaxBudgetUsd", "0")]
    [InlineData("Livemap:AgentMaxBudgetUsd", "-1")]
    [InlineData("Livemap:AgentRunsPerBuild", "0")]
    [InlineData("Livemap:AgentRunsPerBuild", "11")]
    [InlineData("Livemap:AgentDeadlineSeconds", "299")]
    [InlineData("Livemap:AgentDeadlineSeconds", "3001")]
    [InlineData("Livemap:DepotDownloaderUrl", "http://example.com/dd.zip")]
    [InlineData("Livemap:DepotDownloaderUrl", "not a url")]
    [InlineData("Livemap:DepotDownloaderSha256", "ABC")]
    [InlineData("Livemap:DepotDownloaderSha256", "A999DEC66B4850FC961BD50366696D23C2D0FAD7B18790E6A5647B2F19097A53")]
    [InlineData("Livemap:PythonIndexUrl", "nexus/simple")]
    [InlineData("Livemap:PythonIndexUrl", "ftp://nexus/simple")]
    public void TheHardPath_WithAnInvalidAgentSetting_FailsAtStartup(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(With(key, value)));
    }

    [Fact]
    public void WithTheHardPathOff_TheAgentSettingsAreNotChecked()
    {
        using var provider = Build(With("Livemap:AgentRunsPerBuild", "0", hardPath: false));

        Assert.NotNull(provider);
    }

    [Fact]
    public void TheLivemapBudgetKind_HasItsOwnCaps_UnlessTheConfigurationSetsThem()
    {
        using var defaults = Build(Enabled);
        using var set = Build(new Dictionary<string, string?>(Enabled)
        {
            ["ClaudeBudget:Kinds:Livemap:MaxPerSubjectPerDay"] = "9",
            ["ClaudeBudget:Kinds:Livemap:MaxFailedAttemptsPerRevision"] = "6",
        });

        var fallback = defaults.GetRequiredService<IOptions<ClaudeBudgetOptions>>().Value.For(LivemapBudget.Kind);
        var configured = set.GetRequiredService<IOptions<ClaudeBudgetOptions>>().Value.For(LivemapBudget.Kind);

        Assert.Equal(4, fallback.MaxPerSubjectPerDay);
        Assert.Equal(3, fallback.MaxFailedAttemptsPerRevision);
        Assert.Equal(9, configured.MaxPerSubjectPerDay);
        Assert.Equal(6, configured.MaxFailedAttemptsPerRevision);
    }
}
