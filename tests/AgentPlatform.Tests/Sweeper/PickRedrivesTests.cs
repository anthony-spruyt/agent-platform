using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Sweeper;

public class PickRedrivesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ClaudeBudgetStore Store, InMemoryRunData Data) Create(int perHour = 2, int perDay = 8, int perPr = 2)
    {
        var data = InMemoryRunData.Create();
        var options = Options.Create(new RenovateTriageOptions { Claude = new ClaudeBudgetOptions { MaxPerHour = perHour, MaxPerDay = perDay, MaxPerPrPerDay = perPr } });
        return (new ClaudeBudgetStore(data.Factory, options, new FixedTime(Now)), data);
    }

    private static RedriveCandidate C(string repo, int pr) => new(repo, pr);

    private static async Task RunAsync(InMemoryRunData data, string repo, int pr, TimeSpan ago, ClaudeRunOutcome? outcome = ClaudeRunOutcome.Succeeded)
    {
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var at = Now - ago;
        db.ClaudeRuns.Add(new ClaudeRun
        {
            Id = Guid.NewGuid(),
            Repo = repo,
            PrNumber = pr,
            HeadSha = "sha",
            WorkflowId = "wf",
            Attempt = 1,
            ReservedAt = at,
            PodCreatedAt = at,
            FinishedAt = at,
            Outcome = outcome,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task CooldownAsync(InMemoryRunData data, string scope, TimeSpan from)
    {
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        db.ClaudeCooldowns.Add(new ClaudeCooldown { Scope = scope, Until = Now + from, Cause = "usage-limit" });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task WithNothingSpent_TheHourlyCapBoundsTheRedrives_OldestFirst()
    {
        var (store, _) = Create(perHour: 2);

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2), C("o/c", 3)], Ct);

        Assert.Equal([C("o/a", 1), C("o/b", 2)], picked);
    }

    [Fact]
    public async Task RunsAlreadyStartedThisHour_UseUpTheSlots()
    {
        var (store, data) = Create(perHour: 2);
        await RunAsync(data, "o/x", 9, TimeSpan.FromMinutes(10));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2)], Ct);

        Assert.Equal([C("o/a", 1)], picked);
    }

    [Fact]
    public async Task TheDailyCap_BoundsTheRedrives_EvenWhenTheHourIsFree()
    {
        var (store, data) = Create(perHour: 5, perDay: 3);
        await RunAsync(data, "o/x", 8, TimeSpan.FromHours(5));
        await RunAsync(data, "o/x", 9, TimeSpan.FromHours(6));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2)], Ct);

        Assert.Equal([C("o/a", 1)], picked);
    }

    [Fact]
    public async Task AnActiveGlobalCooldown_PicksNothing()
    {
        var (store, data) = Create();
        await CooldownAsync(data, ClaudeCooldown.Global, TimeSpan.FromMinutes(30));

        Assert.Empty(await store.PickRedrivesAsync([C("o/a", 1)], Ct));
    }

    [Fact]
    public async Task AnExpiredCooldown_DoesNotBlock()
    {
        var (store, data) = Create();
        await CooldownAsync(data, ClaudeCooldown.Global, TimeSpan.FromMinutes(-1));

        Assert.Single(await store.PickRedrivesAsync([C("o/a", 1)], Ct));
    }

    [Fact]
    public async Task ARepoCooldownAndAnOpenBreaker_SkipThatRepoOnly()
    {
        var (store, data) = Create(perHour: 5, perDay: 20);
        await CooldownAsync(data, "o/cool", TimeSpan.FromMinutes(30));
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(data, "o/broken", 50 + i, TimeSpan.FromMinutes(10 + i), ClaudeRunOutcome.PodFailed);
        }

        var picked = await store.PickRedrivesAsync([C("o/cool", 1), C("o/broken", 2), C("o/fine", 3)], Ct);

        Assert.Equal([C("o/fine", 3)], picked);
    }

    [Fact]
    public async Task APrAtItsDailyCap_IsSkipped_AndDoesNotUseASlot()
    {
        var (store, data) = Create(perHour: 5, perDay: 20, perPr: 2);
        await RunAsync(data, "o/a", 1, TimeSpan.FromHours(3));
        await RunAsync(data, "o/a", 1, TimeSpan.FromHours(4));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2)], Ct);

        Assert.Equal([C("o/b", 2)], picked);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
