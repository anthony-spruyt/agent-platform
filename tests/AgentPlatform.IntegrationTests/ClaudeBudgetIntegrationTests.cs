using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentPlatform.IntegrationTests;

[Collection(AppHostSharedFixture.Name)]
public class ClaudeBudgetIntegrationTests(AppHostFixture fixture)
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Model = "opus";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private sealed class FixedFactory(DbContextOptions<RunDataDbContext> options) : IDbContextFactory<RunDataDbContext>
    {
        public RunDataDbContext CreateDbContext() => new(options);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public required ManualTime Time { get; init; }

        public required IDbContextFactory<RunDataDbContext> Factory { get; init; }

        public required ClaudeBudgetStore Budget { get; init; }

        public required TriagePendingStore Pending { get; init; }

        public async Task<ClaudeReservation> ReserveAsync(int pr, string sha = Sha, string repo = Repo, Guid? runId = null, CancellationToken ct = default) =>
            await Budget.ReserveAsync("wf-" + pr, new ReserveClaudeRunInput(runId ?? Guid.NewGuid(), repo, pr, sha, Model), ct);

        public async Task<Guid> RunAsync(int pr, ClaudeRunOutcome outcome, string sha = Sha, string repo = Repo, CancellationToken ct = default)
        {
            await Pending.UpsertAsync(repo, pr, sha, "ci-failed", ct);
            var reservation = await ReserveAsync(pr, sha, repo, ct: ct);
            Assert.Equal(ClaudeReservationKind.Reserved, reservation.Kind);
            await Budget.MarkPodCreatedAsync(reservation.RunId!.Value, ct);
            await Budget.FinishAsync(new FinishClaudeRunInput(reservation.RunId.Value, outcome), ct);
            return reservation.RunId.Value;
        }

        public async Task<ClaudeRun> RunRowAsync(Guid id, CancellationToken ct)
        {
            await using var db = await Factory.CreateDbContextAsync(ct);
            return await db.ClaudeRuns.SingleAsync(r => r.Id == id, ct);
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);

    private async Task<Harness> StartAsync(CancellationToken ct, ClaudeBudgetOptions? caps = null)
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(await fixture.CreateEmptyDatabaseAsync(ct)).Options;
        var factory = new FixedFactory(options);
        Assert.Equal(0, await new RunDataMigrator(factory, NullLogger<RunDataMigrator>.Instance).RunAsync(ct));
        var time = new ManualTime(Start);
        var triage = Options.Create(new RenovateTriageOptions { Claude = caps ?? new ClaudeBudgetOptions() });
        return new Harness
        {
            Time = time,
            Factory = factory,
            Budget = new ClaudeBudgetStore(factory, triage, time),
            Pending = new TriagePendingStore(factory, time),
        };
    }

    [Fact]
    public async Task Reserve_WithNoHistory_ReservesTheFirstAttempt()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        var runId = Guid.NewGuid();

        var reservation = await h.ReserveAsync(7, runId: runId, ct: ct);

        Assert.Equal(ClaudeReservation.Reserved(runId, 1), reservation);
        var row = await h.RunRowAsync(runId, ct);
        Assert.Equal((Repo, 7, Sha, "wf-7", Model, Start), (row.Repo, row.PrNumber, row.HeadSha, row.WorkflowId, row.Model, row.ReservedAt));
        Assert.Null(row.PodCreatedAt);
        Assert.Null(row.FinishedAt);
    }

    [Fact]
    public async Task Reserve_TheSameRunIdTwice_IsOneReservation()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        var runId = Guid.NewGuid();

        var first = await h.ReserveAsync(7, runId: runId, ct: ct);
        var second = await h.ReserveAsync(7, runId: runId, ct: ct);

        Assert.Equal(first, second);
        await using var db = await h.Factory.CreateDbContextAsync(ct);
        Assert.Equal(1, await db.ClaudeRuns.CountAsync(ct));
    }

    [Fact]
    public async Task Reserve_AtTheHourlyCap_ParksUntilTheOldestRunLeavesTheWindow()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.RunAsync(1, ClaudeRunOutcome.Succeeded, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(10);
        await h.RunAsync(2, ClaudeRunOutcome.Succeeded, ct: ct);

        var parked = await h.ReserveAsync(3, ct: ct);

        Assert.Equal(ClaudeReservation.Park(ClaudeBudgetCauses.HourlyCap, Start.AddHours(1)), parked);
        h.Time.Now = Start.AddMinutes(61);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(3, ct: ct)).Kind);
    }

    [Fact]
    public async Task Reserve_AtTheDailyCap_ParksUntilTheOldestRunLeavesTheDay()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 3, MaxPerPrPerDay = 20 });
        for (var pr = 1; pr <= 3; pr++)
        {
            await h.RunAsync(pr, ClaudeRunOutcome.Succeeded, ct: ct);
            h.Time.Now += TimeSpan.FromHours(2);
        }

        var parked = await h.ReserveAsync(4, ct: ct);

        Assert.Equal(ClaudeReservation.Park(ClaudeBudgetCauses.DailyCap, Start.AddHours(24)), parked);
        h.Time.Now = Start.AddHours(24).AddMinutes(1);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(4, ct: ct)).Kind);
    }

    [Fact]
    public async Task Reserve_AtThePerPrCap_ParksThatPrOnlyAcrossHeads()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 2 });
        await h.RunAsync(7, ClaudeRunOutcome.Succeeded, ct: ct);
        h.Time.Now += TimeSpan.FromHours(1);
        await h.RunAsync(7, ClaudeRunOutcome.Succeeded, new string('a', 40), ct: ct);

        var parked = await h.ReserveAsync(7, new string('b', 40), ct: ct);

        Assert.Equal(ClaudeReservation.Park(ClaudeBudgetCauses.PrDailyCap, Start.AddHours(24)), parked);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(8, ct: ct)).Kind);
    }

    [Theory]
    [InlineData(ClaudeRunOutcome.Succeeded)]
    [InlineData(ClaudeRunOutcome.PodFailed)]
    [InlineData(ClaudeRunOutcome.LimitStop)]
    [InlineData(ClaudeRunOutcome.Cancelled)]
    public async Task EveryRunThatCreatedAPodCounts_WhateverTheOutcome(ClaudeRunOutcome outcome)
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 1, MaxPerDay = 8, MaxPerPrPerDay = 2 });
        await h.RunAsync(1, outcome, ct: ct);

        var parked = await h.ReserveAsync(2, ct: ct);

        Assert.Equal(ClaudeReservationKind.Park, parked.Kind);
    }

    [Fact]
    public async Task AReservationThatNeverCreatedAPod_DoesNotCount()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 1, MaxPerDay = 8, MaxPerPrPerDay = 2 });
        var first = await h.ReserveAsync(1, ct: ct);
        await h.Budget.FinishAsync(new FinishClaudeRunInput(first.RunId!.Value, ClaudeRunOutcome.PodFailed), ct);

        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(2, ct: ct)).Kind);
    }

    [Fact]
    public async Task AnOpenReservation_CountsUntilItsLeaseEnds()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 1, MaxPerDay = 8, MaxPerPrPerDay = 2 });
        await h.ReserveAsync(1, ct: ct);

        Assert.Equal(ClaudeReservationKind.Park, (await h.ReserveAsync(2, ct: ct)).Kind);

        h.Time.Now += ClaudeBudgetStore.OpenReservationLease + TimeSpan.FromMinutes(1);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(2, ct: ct)).Kind);
    }

    [Fact]
    public async Task ConcurrentReservations_NeverExceedTheHourlyCap()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 2, MaxPerDay = 8, MaxPerPrPerDay = 2 });

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(pr => h.ReserveAsync(pr, ct: ct)));

        Assert.Equal(2, results.Count(r => r.Kind == ClaudeReservationKind.Reserved));
        Assert.Equal(6, results.Count(r => r.Kind == ClaudeReservationKind.Park));
    }

    [Fact]
    public async Task Reserve_DuringAGlobalCooldown_ParksUntilItEnds()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, Start.AddHours(3), ClaudeBudgetCauses.UsageLimit), ct);

        var parked = await h.ReserveAsync(1, ct: ct);

        Assert.Equal(ClaudeReservation.Park(ClaudeBudgetCauses.UsageLimit, Start.AddHours(3)), parked);
        h.Time.Now = Start.AddHours(3).AddMinutes(1);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(1, ct: ct)).Kind);
    }

    [Fact]
    public async Task ARepoCooldown_ParksThatRepoOnly()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(Repo, Start.AddHours(2), "repo-hold"), ct);

        Assert.Equal(ClaudeReservation.Park("repo-hold", Start.AddHours(2)), await h.ReserveAsync(1, ct: ct));
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(1, repo: "anthony-spruyt/other", ct: ct)).Kind);
    }

    [Fact]
    public async Task ThreeConsecutiveFailedRuns_OpenTheRepoBreakerForAnHour()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        for (var pr = 1; pr <= 3; pr++)
        {
            await h.RunAsync(pr, ClaudeRunOutcome.TimedOut, ct: ct);
        }

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen), await h.ReserveAsync(4, ct: ct));
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(4, repo: "anthony-spruyt/other", ct: ct)).Kind);

        h.Time.Now += TimeSpan.FromMinutes(61);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(4, ct: ct)).Kind);
    }

    [Fact]
    public async Task ASuccessfulRun_ResetsTheConsecutiveFailures()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(1, ClaudeRunOutcome.NoResult, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.RunAsync(2, ClaudeRunOutcome.Succeeded, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.RunAsync(3, ClaudeRunOutcome.NoResult, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.RunAsync(4, ClaudeRunOutcome.NoResult, ct: ct);

        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(5, ct: ct)).Kind);
    }

    [Fact]
    public async Task ACancelledRun_NeitherOpensNorClosesTheBreaker()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(1, ClaudeRunOutcome.PodFailed, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.RunAsync(2, ClaudeRunOutcome.PodFailed, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.RunAsync(3, ClaudeRunOutcome.Cancelled, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(ClaudeReservationKind.Reserved, (await h.ReserveAsync(4, ct: ct)).Kind);
        await h.RunAsync(4, ClaudeRunOutcome.PodFailed, ct: ct);

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen), await h.ReserveAsync(5, ct: ct));
    }

    [Fact]
    public async Task TwoFailedAttemptsOnAHead_SendTheHeadToTheOwner()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(7, ClaudeRunOutcome.PodFailed, ct: ct);
        var second = await h.ReserveAsync(7, ct: ct);
        Assert.Equal(2, second.Attempt);
        await h.Budget.MarkPodCreatedAsync(second.RunId!.Value, ct);
        await h.Budget.FinishAsync(new FinishClaudeRunInput(second.RunId.Value, ClaudeRunOutcome.InvalidResult), ct);

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted), await h.ReserveAsync(7, ct: ct));
    }

    [Fact]
    public async Task ANewHead_StartsAtTheFirstAttemptAgain()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(7, ClaudeRunOutcome.PodFailed, ct: ct);
        await h.RunAsync(7, ClaudeRunOutcome.PodFailed, ct: ct);
        var moved = new string('c', 40);
        await h.Pending.UpsertAsync(Repo, 7, moved, "ci-failed", ct);

        Assert.Equal(1, (await h.ReserveAsync(7, moved, ct: ct)).Attempt);
    }

    [Fact]
    public async Task ACancelledRun_IsNotAFailedAttempt()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(7, ClaudeRunOutcome.Cancelled, ct: ct);

        Assert.Equal(1, (await h.ReserveAsync(7, ct: ct)).Attempt);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(30, 30)]
    [InlineData(60 * 24 * 3, 60 * 24)]
    public async Task SetCooldown_ClampsTheResetToFifteenMinutesToADay(int resetsInMinutes, int expectedMinutes)
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);

        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, Start.AddMinutes(resetsInMinutes), ClaudeBudgetCauses.UsageLimit), ct);

        await using var db = await h.Factory.CreateDbContextAsync(ct);
        Assert.Equal(Start.AddMinutes(expectedMinutes), (await db.ClaudeCooldowns.SingleAsync(ct)).Until);
    }

    [Fact]
    public async Task SetCooldown_WithoutAReset_LastsAnHour()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);

        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, null, ClaudeBudgetCauses.UsageLimit), ct);

        await using var db = await h.Factory.CreateDbContextAsync(ct);
        Assert.Equal(Start.AddHours(1), (await db.ClaudeCooldowns.SingleAsync(ct)).Until);
    }

    [Fact]
    public async Task SetCooldown_NeverShortensAnActiveOne()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, Start.AddHours(5), ClaudeBudgetCauses.UsageLimit), ct);

        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, Start.AddHours(1), ClaudeBudgetCauses.UsageLimit), ct);

        await using var db = await h.Factory.CreateDbContextAsync(ct);
        Assert.Equal(Start.AddHours(5), (await db.ClaudeCooldowns.SingleAsync(ct)).Until);
    }

    [Fact]
    public async Task CountActiveCooldowns_SkipsEndedOnes()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, Start.AddHours(1), ClaudeBudgetCauses.UsageLimit), ct);
        await h.Budget.SetCooldownAsync(new SetClaudeCooldownInput(Repo, Start.AddHours(3), "repo-hold"), ct);

        Assert.Equal(2, await h.Budget.CountActiveCooldownsAsync(ct));
        h.Time.Now = Start.AddHours(2);
        Assert.Equal(1, await h.Budget.CountActiveCooldownsAsync(ct));
    }

    [Fact]
    public async Task MarkPodCreated_StampsTheFirstTimeOnly()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        var reservation = await h.ReserveAsync(1, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.Budget.MarkPodCreatedAsync(reservation.RunId!.Value, ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.Budget.MarkPodCreatedAsync(reservation.RunId.Value, ct);

        Assert.Equal(Start.AddMinutes(1), (await h.RunRowAsync(reservation.RunId.Value, ct)).PodCreatedAt);
    }

    [Fact]
    public async Task MarkPodCreatedAndFinish_ForAnUnknownRun_Throw()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);

        await Assert.ThrowsAsync<UnknownClaudeRunException>(() => h.Budget.MarkPodCreatedAsync(Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<UnknownClaudeRunException>(() => h.Budget.FinishAsync(new FinishClaudeRunInput(Guid.NewGuid(), ClaudeRunOutcome.Succeeded), ct));
    }

    [Fact]
    public async Task Finish_RecordsTheOutcomeUsageAndCostOnce()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        var reservation = await h.ReserveAsync(1, ct: ct);
        h.Time.Now += TimeSpan.FromMinutes(3);
        await h.Budget.FinishAsync(new FinishClaudeRunInput(reservation.RunId!.Value, ClaudeRunOutcome.Succeeded, "claude-opus-4", 120, 30, 0.42m), ct);
        h.Time.Now += TimeSpan.FromMinutes(1);
        await h.Budget.FinishAsync(new FinishClaudeRunInput(reservation.RunId.Value, ClaudeRunOutcome.PodFailed), ct);

        var row = await h.RunRowAsync(reservation.RunId.Value, ct);
        Assert.Equal(ClaudeRunOutcome.Succeeded, row.Outcome);
        Assert.Equal(Start.AddMinutes(3), row.FinishedAt);
        Assert.Equal(("claude-opus-4", 120L, 30L, 0.42m), (row.Model, row.InputTokens, row.OutputTokens, row.CostUsd));
    }

    [Fact]
    public async Task Finish_CountsAFailureOnTheMatchingPendingHeadOnly()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        await h.Pending.UpsertAsync(Repo, 7, Sha, "ci-failed", ct);
        var reservation = await h.ReserveAsync(7, ct: ct);
        var moved = new string('d', 40);
        await h.Pending.UpsertAsync(Repo, 7, moved, "ci-failed", ct);

        await h.Budget.FinishAsync(new FinishClaudeRunInput(reservation.RunId!.Value, ClaudeRunOutcome.PodFailed), ct);

        Assert.Equal(0, (await h.Pending.FindAsync(Repo, 7, ct))!.Attempts);
    }

    [Fact]
    public async Task Finish_CountsOnlyFailuresAsAttempts()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct, new ClaudeBudgetOptions { MaxPerHour = 20, MaxPerDay = 20, MaxPerPrPerDay = 20 });
        await h.RunAsync(7, ClaudeRunOutcome.Succeeded, ct: ct);
        await h.RunAsync(7, ClaudeRunOutcome.Cancelled, ct: ct);
        Assert.Equal(0, (await h.Pending.FindAsync(Repo, 7, ct))!.Attempts);

        await h.RunAsync(7, ClaudeRunOutcome.Truncated, ct: ct);

        Assert.Equal(1, (await h.Pending.FindAsync(Repo, 7, ct))!.Attempts);
    }

    [Fact]
    public async Task ConcurrentPendingUpserts_ForOnePr_LeaveOneRow()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => h.Pending.UpsertAsync(Repo, 7, Sha, "ci-failed", ct)));

        await using var db = await h.Factory.CreateDbContextAsync(ct);
        Assert.Equal(1, await db.TriagePending.CountAsync(ct));
    }

    [Fact]
    public async Task AClaudeVerdict_IsStoredOncePerHeadWithItsRungAndAction()
    {
        using var _ = Scope(out var ct);
        var h = await StartAsync(ct);
        var verdicts = new TriageVerdictStore(h.Factory);

        Assert.True(await verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "claude-safe", null), TriageMode.Live, ct, LadderRung.Claude));
        Assert.False(await verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Fixable, "claude-fixable", null), TriageMode.Live, ct, LadderRung.Claude));
        await verdicts.MarkAppliedAsync(Repo, 7, Sha, TriageVerdict.Safe, ct, VerdictAction.Approve);

        await using var db = await h.Factory.CreateDbContextAsync(ct);
        var row = await db.TriageVerdicts.SingleAsync(ct);
        Assert.Equal((TriageVerdict.Safe, LadderRung.Claude, VerdictAction.Approve), (row.Verdict, row.Rung, row.Action));
    }

    private static CancellationTokenSource Scope(out CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TestTimeout);
        ct = source.Token;
        return source;
    }
}
