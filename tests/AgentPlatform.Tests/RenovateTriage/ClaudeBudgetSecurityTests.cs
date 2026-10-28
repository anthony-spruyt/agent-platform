using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeBudgetSecurityTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ClaudeBudgetStore Store, InMemoryRunData Data) Create(int perHour = 2, int perDay = 8, int perPr = 20) =>
        InMemoryRunData.CreateWithBudget(Now, perHour, perDay, perPr);

    private static Task<ClaudeReservation> ReserveAsync(ClaudeBudgetStore store, int pr, bool security, string repo = Repo) =>
        store.ReserveAsync($"wf-{pr}", new ReserveClaudeRunInput(Guid.NewGuid(), repo, pr, Sha, "opus", security), Ct);

    private static Task RunAsync(InMemoryRunData data, int pr, TimeSpan ago, ClaudeRunOutcome outcome = ClaudeRunOutcome.Succeeded, string repo = Repo) =>
        data.SeedRunAsync(repo, pr, Now - ago, Ct, outcome);

    private static Task CooldownAsync(InMemoryRunData data, string scope) =>
        data.SeedCooldownAsync(scope, Now.AddMinutes(30), Ct);

    [Fact]
    public async Task ASecurityPullRequest_ReservesWhenTheHourlyCapIsFull_AndOthersPark()
    {
        var (store, data) = Create(perHour: 2);
        await RunAsync(data, 8, TimeSpan.FromMinutes(5));
        await RunAsync(data, 9, TimeSpan.FromMinutes(10));

        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, 1, security: true)).Kind);
        Assert.Equal(ClaudeBudgetCauses.HourlyCap, (await ReserveAsync(store, 2, security: false)).Cause);
    }

    [Fact]
    public async Task ASecurityPullRequest_ReservesWhenTheDailyCapIsFull_AndOthersPark()
    {
        var (store, data) = Create(perHour: 20, perDay: 2);
        await RunAsync(data, 8, TimeSpan.FromHours(5));
        await RunAsync(data, 9, TimeSpan.FromHours(6));

        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, 1, security: true)).Kind);
        Assert.Equal(ClaudeBudgetCauses.DailyCap, (await ReserveAsync(store, 2, security: false)).Cause);
    }

    [Fact]
    public async Task ASecurityPullRequest_ReservesWhenBothCapsAreFull()
    {
        var (store, data) = Create(perHour: 2, perDay: 2);
        await RunAsync(data, 8, TimeSpan.FromMinutes(5));
        await RunAsync(data, 9, TimeSpan.FromMinutes(10));

        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, 1, security: true)).Kind);
    }

    [Fact]
    public async Task ASecurityRun_IsRecorded_AndCountsTowardTheCapsOthersSee()
    {
        var (store, data) = Create(perHour: 1, perDay: 8);
        await RunAsync(data, 8, TimeSpan.FromMinutes(5));

        var reserved = await ReserveAsync(store, 1, security: true);

        Assert.Equal(ClaudeReservationKind.Reserved, reserved.Kind);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(2, await db.ClaudeRuns.CountAsync(Ct));
        Assert.Contains(await db.ClaudeRuns.ToListAsync(Ct), r => r.Id == reserved.RunId && r.PrNumber == 1);
        Assert.Equal(ClaudeBudgetCauses.HourlyCap, (await ReserveAsync(store, 2, security: false)).Cause);
    }

    [Fact]
    public async Task ASecurityPullRequest_ParksOnTheGlobalCooldown()
    {
        var (store, data) = Create();
        await CooldownAsync(data, ClaudeCooldown.Global);

        var parked = await ReserveAsync(store, 1, security: true);

        Assert.Equal((ClaudeReservationKind.Park, "usage-limit"), (parked.Kind, parked.Cause));
    }

    [Fact]
    public async Task ASecurityPullRequest_ParksOnItsRepoCooldownOnly()
    {
        var (store, data) = Create();
        await CooldownAsync(data, Repo);

        Assert.Equal(ClaudeReservationKind.Park, (await ReserveAsync(store, 1, security: true)).Kind);
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, 1, security: true, repo: "anthony-spruyt/other")).Kind);
    }

    [Fact]
    public async Task ASecurityPullRequest_ParksOnThePerPrDailyCap_EvenWhenTheHourlyCapIsFullToo()
    {
        var (store, data) = Create(perHour: 2, perDay: 8, perPr: 2);
        await RunAsync(data, 1, TimeSpan.FromMinutes(5));
        await RunAsync(data, 1, TimeSpan.FromMinutes(10));

        var parked = await ReserveAsync(store, 1, security: true);

        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.PrDailyCap), (parked.Kind, parked.Cause));
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, 2, security: true)).Kind);
    }

    [Fact]
    public async Task ASecurityPullRequest_GoesToTheOwnerWhenTheRepoBreakerIsOpen()
    {
        var (store, data) = Create(perHour: 20, perDay: 20);
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(data, 50 + i, TimeSpan.FromMinutes(5 + i), ClaudeRunOutcome.PodFailed);
        }

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen), await ReserveAsync(store, 1, security: true));
    }

    [Fact]
    public async Task ASecurityHead_WithTwoFailedAttempts_GoesToTheOwner()
    {
        var (store, data) = Create();
        await data.Pending.UpsertAsync(Repo, 1, Sha, "claude-pending", Ct);
        await using (var db = await data.Factory.CreateDbContextAsync(Ct))
        {
            var row = await db.TriagePending.SingleAsync(Ct);
            row.Attempts = 2;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted), await ReserveAsync(store, 1, security: true));
    }

    [Fact]
    public async Task ANonSecurityPullRequest_ParksAtTheHourlyCap()
    {
        var (store, data) = Create(perHour: 1, perDay: 8);
        await RunAsync(data, 8, TimeSpan.FromMinutes(5));

        var parked = await ReserveAsync(store, 1, security: false);

        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.HourlyCap), (parked.Kind, parked.Cause));
    }
}
