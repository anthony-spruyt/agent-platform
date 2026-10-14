using AgentPlatform.Host;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeBudgetConfigurationTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

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
    public void TheCaps_DefaultToTwoAnHourEightADayAndTwoPerPr()
    {
        using var provider = Build();

        var caps = provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value.Claude;

        Assert.Equal((2, 8, 2), (caps.MaxPerHour, caps.MaxPerDay, caps.MaxPerPrPerDay));
    }

    [Fact]
    public void TheCapsAndTheOwnerLogin_BindFromConfiguration()
    {
        using var provider = Build(new()
        {
            ["RenovateTriage:Claude:MaxPerHour"] = "3",
            ["RenovateTriage:Claude:MaxPerDay"] = "12",
            ["RenovateTriage:Claude:MaxPerPrPerDay"] = "4",
            ["RenovateTriage:OwnerLogin"] = "anthony-spruyt",
        });

        var options = provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value;

        Assert.Equal((3, 12, 4, "anthony-spruyt"), (options.Claude.MaxPerHour, options.Claude.MaxPerDay, options.Claude.MaxPerPrPerDay, options.OwnerLogin));
    }

    [Theory]
    [InlineData("MaxPerHour", "0")]
    [InlineData("MaxPerDay", "0")]
    [InlineData("MaxPerPrPerDay", "-1")]
    public void ACapBelowOne_FailsAtStartup(string cap, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new() { [$"RenovateTriage:Claude:{cap}"] = value }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-owner")]
    [InlineData("owner-")]
    [InlineData("two words")]
    [InlineData("owner/repo")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AMissingOrMalformedOwnerLogin_FailsAtStartup(string? login)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["RenovateTriage:OwnerLogin"] = login }));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("anthony-spruyt")]
    [InlineData("A1-b2")]
    public void AGitHubLogin_IsAccepted(string login)
    {
        using var provider = Build(new() { ["RenovateTriage:OwnerLogin"] = login });

        Assert.Equal(login, provider.GetRequiredService<IOptions<RenovateTriageOptions>>().Value.OwnerLogin);
    }

    [Fact]
    public void TheWorkerRunsTheBudgetActivitiesAndHoldsTheStores()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get(TaskQueues.AgentPlatform);

        foreach (var name in new[] { ClaudeBudgetActivities.ReserveName, ClaudeBudgetActivities.MarkPodCreatedName, ClaudeBudgetActivities.FinishName, ClaudeBudgetActivities.SetCooldownName })
        {
            Assert.Contains(options.Activities, a => a.Name == name);
        }

        Assert.NotNull(provider.GetService<ClaudeBudgetStore>());
        Assert.NotNull(provider.GetService<TriagePendingStore>());
        Assert.NotNull(provider.GetService<ClaudeBudgetActivities>());
    }

    [Fact]
    public async Task Reserve_OutsideAWorkflow_FailsWithoutRetry()
    {
        using var provider = Build();
        var activities = provider.GetRequiredService<ClaudeBudgetActivities>();
        var environment = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { WorkflowId = null } };

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => environment.RunAsync(() => activities.ReserveAsync(new ReserveClaudeRunInput(Guid.NewGuid(), "anthony-spruyt/widgets", 7, Sha, "opus"))));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task Reserve_WithAnInvalidInput_FailsWithoutRetry()
    {
        using var provider = Build();
        var activities = provider.GetRequiredService<ClaudeBudgetActivities>();
        var environment = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "wf-1" } };

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => environment.RunAsync(() => activities.ReserveAsync(new ReserveClaudeRunInput(Guid.NewGuid(), "anthony-spruyt/widgets", 0, Sha, "opus"))));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task SetCooldown_WithoutAScope_FailsWithoutRetry()
    {
        using var provider = Build();
        var activities = provider.GetRequiredService<ClaudeBudgetActivities>();

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activities.SetCooldownAsync(new SetClaudeCooldownInput(" ", null, "usage-limit"))));

        Assert.True(failure.NonRetryable);
    }

    [Theory]
    [InlineData(ClaudeRunOutcome.Succeeded, false)]
    [InlineData(ClaudeRunOutcome.Cancelled, false)]
    [InlineData(ClaudeRunOutcome.PodFailed, true)]
    [InlineData(ClaudeRunOutcome.TimedOut, true)]
    [InlineData(ClaudeRunOutcome.Truncated, true)]
    [InlineData(ClaudeRunOutcome.NoResult, true)]
    [InlineData(ClaudeRunOutcome.InvalidResult, true)]
    [InlineData(ClaudeRunOutcome.LimitStop, true)]
    public void OnlyTheListedOutcomesAreFailures(ClaudeRunOutcome outcome, bool failure) =>
        Assert.Equal(failure, outcome.IsFailure());

    [Fact]
    public void TheReservation_RoundTripsThroughTheTemporalPayloadJson()
    {
        var reservation = ClaudeReservation.Park(ClaudeBudgetCauses.HourlyCap, new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero));

        var json = System.Text.Json.JsonSerializer.Serialize(reservation);

        Assert.Contains("\"Park\"", json, StringComparison.Ordinal);
        Assert.Equal(reservation, System.Text.Json.JsonSerializer.Deserialize<ClaudeReservation>(json));
    }
}
