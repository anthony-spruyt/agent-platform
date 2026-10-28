using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.Sweeper;

public class PickRedrivesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ClaudeBudgetStore Store, InMemoryRunData Data) Create(int perHour = 2, int perDay = 8, int perPr = 2) =>
        InMemoryRunData.CreateWithBudget(Now, perHour, perDay, perPr);

    private static RedriveCandidate C(string repo, int pr) => new(repo, pr);

    private static RedriveCandidate S(string repo, int pr) => new(repo, pr, Security: true);

    private static Task RunAsync(InMemoryRunData data, string repo, int pr, TimeSpan ago, ClaudeRunOutcome? outcome = ClaudeRunOutcome.Succeeded) =>
        data.SeedRunAsync(repo, pr, Now - ago, Ct, outcome);

    private static Task CooldownAsync(InMemoryRunData data, string scope, TimeSpan from) =>
        data.SeedCooldownAsync(scope, Now + from, Ct);

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

    [Fact]
    public async Task ASecurityCandidate_GoesFirst_AheadOfOlderOnes()
    {
        var (store, _) = Create(perHour: 5);

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2), S("o/c", 3)], Ct);

        Assert.Equal([S("o/c", 3), C("o/a", 1), C("o/b", 2)], picked);
    }

    [Fact]
    public async Task SecurityCandidates_KeepTheirOldestFirstOrder_AndSoDoTheRest()
    {
        var (store, _) = Create(perHour: 5);

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/b", 2), C("o/c", 3), S("o/d", 4)], Ct);

        Assert.Equal([S("o/b", 2), S("o/d", 4), C("o/a", 1), C("o/c", 3)], picked);
    }

    [Fact]
    public async Task ASecurityCandidate_IsPicked_WhenTheHourlyCapIsFull()
    {
        var (store, data) = Create(perHour: 2);
        await RunAsync(data, "o/x", 8, TimeSpan.FromMinutes(5));
        await RunAsync(data, "o/x", 9, TimeSpan.FromMinutes(10));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/b", 2)], Ct);

        Assert.Equal([S("o/b", 2)], picked);
    }

    [Fact]
    public async Task ASecurityCandidate_IsPicked_WhenTheDailyCapIsFull()
    {
        var (store, data) = Create(perHour: 5, perDay: 2);
        await RunAsync(data, "o/x", 8, TimeSpan.FromHours(5));
        await RunAsync(data, "o/x", 9, TimeSpan.FromHours(6));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/b", 2)], Ct);

        Assert.Equal([S("o/b", 2)], picked);
    }

    [Fact]
    public async Task ASecurityCandidate_IsPicked_WhenTheHourIsAlreadyOverspent()
    {
        var (store, data) = Create(perHour: 2);
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(data, "o/x", 20 + i, TimeSpan.FromMinutes(5 + i));
        }

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/b", 2)], Ct);

        Assert.Equal([S("o/b", 2)], picked);
    }

    [Fact]
    public async Task EachPickedSecurityCandidate_UsesOneSlot_SoTheOthersOnlyFillWhatRemains()
    {
        var (store, _) = Create(perHour: 3);

        var picked = await store.PickRedrivesAsync([C("o/a", 1), C("o/b", 2), S("o/c", 3), S("o/d", 4)], Ct);

        Assert.Equal([S("o/c", 3), S("o/d", 4), C("o/a", 1)], picked);
    }

    [Fact]
    public async Task WhenSecurityCandidatesTakeEverySlot_NoOtherCandidateIsPicked()
    {
        var (store, _) = Create(perHour: 2);

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/b", 2), S("o/c", 3)], Ct);

        Assert.Equal([S("o/b", 2), S("o/c", 3)], picked);
    }

    [Fact]
    public async Task ASecurityCandidateThatIsSkipped_DoesNotUseASlot()
    {
        var (store, data) = Create(perHour: 1, perDay: 20);
        await CooldownAsync(data, "o/cool", TimeSpan.FromMinutes(30));

        var picked = await store.PickRedrivesAsync([C("o/a", 1), S("o/cool", 2)], Ct);

        Assert.Equal([C("o/a", 1)], picked);
    }

    [Fact]
    public async Task ASecurityCandidate_IsStillSubjectToTheGlobalCooldown()
    {
        var (store, data) = Create();
        await CooldownAsync(data, ClaudeCooldown.Global, TimeSpan.FromMinutes(30));

        Assert.Empty(await store.PickRedrivesAsync([S("o/a", 1), C("o/b", 2)], Ct));
    }

    [Fact]
    public async Task ASecurityCandidate_IsStillSubjectToTheRepoCooldownAndTheBreaker()
    {
        var (store, data) = Create(perHour: 1, perDay: 20);
        await CooldownAsync(data, "o/cool", TimeSpan.FromMinutes(30));
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(data, "o/broken", 50 + i, TimeSpan.FromMinutes(10 + i), ClaudeRunOutcome.PodFailed);
        }

        var picked = await store.PickRedrivesAsync([S("o/cool", 1), S("o/broken", 2), S("o/fine", 3)], Ct);

        Assert.Equal([S("o/fine", 3)], picked);
    }

    [Fact]
    public async Task ASecurityCandidate_IsStillSubjectToThePerPrDailyCap()
    {
        var (store, data) = Create(perHour: 5, perDay: 20, perPr: 2);
        await RunAsync(data, "o/a", 1, TimeSpan.FromHours(3));
        await RunAsync(data, "o/a", 1, TimeSpan.FromHours(4));

        var picked = await store.PickRedrivesAsync([S("o/a", 1), S("o/b", 2)], Ct);

        Assert.Equal([S("o/b", 2)], picked);
    }
}
