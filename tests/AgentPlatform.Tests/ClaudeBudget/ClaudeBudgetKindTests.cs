using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.ClaudeBudget;

public class ClaudeBudgetKindTests
{
    private const string KindA = "kind-a";
    private const string KindB = "kind-b";
    private const string Subject = "subject-1";
    private const string Scope = "scope-1";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ClaudeBudgetStore Store, InMemoryRunData Data) Create(
        int perHour = 20,
        int perDay = 20,
        Action<ClaudeBudgetOptions>? configure = null,
        params IClaudeAttemptLedger[] ledgers)
    {
        var data = InMemoryRunData.Create();
        var options = new ClaudeBudgetOptions { MaxPerHour = perHour, MaxPerDay = perDay };
        configure?.Invoke(options);
        return (new ClaudeBudgetStore(data.Factory, Options.Create(options), new FixedTimeProvider(Now), ledgers), data);
    }

    private static Task<ClaudeReservation> ReserveAsync(ClaudeBudgetStore store, string kind, string subject = Subject, string scope = Scope, string? revision = "rev-1", bool exempt = false) =>
        store.ReserveAsync("wf", new ReserveClaudeRunInput(Guid.NewGuid(), kind, subject, scope, revision, "opus", exempt), Ct);

    private static async Task SeedAsync(InMemoryRunData data, string kind, TimeSpan ago, string subject = Subject, string scope = Scope, string? revision = "rev-1", ClaudeRunOutcome outcome = ClaudeRunOutcome.Succeeded)
    {
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        db.ClaudeRuns.Add(new ClaudeRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Subject = subject,
            Scope = scope,
            Revision = revision,
            WorkflowId = "wf",
            Attempt = 1,
            ReservedAt = Now - ago,
            PodCreatedAt = Now - ago,
            FinishedAt = Now - ago,
            Outcome = outcome,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AnotherKindsSpend_DoesNotCountAgainstTheSubjectCap()
    {
        var (store, data) = Create(configure: o => o.Kinds[KindA] = new ClaudeKindOptions { MaxPerSubjectPerDay = 1 });
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(5));
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(6));

        var first = await ReserveAsync(store, KindA);
        var second = await ReserveAsync(store, KindA);

        Assert.Equal(ClaudeReservationKind.Reserved, first.Kind);
        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.PrDailyCap), (second.Kind, second.Cause));
    }

    [Fact]
    public async Task AnotherKindsSpend_CountsAgainstTheHourlyCap()
    {
        var (store, data) = Create(perHour: 2);
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(5), subject: "other-1");
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(10), subject: "other-2");

        var parked = await ReserveAsync(store, KindA);

        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.HourlyCap), (parked.Kind, parked.Cause));
    }

    [Fact]
    public async Task AnotherKindsSpend_CountsAgainstTheDailyCap()
    {
        var (store, data) = Create(perDay: 2);
        await SeedAsync(data, KindB, TimeSpan.FromHours(5), subject: "other-1");
        await SeedAsync(data, KindB, TimeSpan.FromHours(6), subject: "other-2");

        var parked = await ReserveAsync(store, KindA);

        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.DailyCap), (parked.Kind, parked.Cause));
    }

    [Fact]
    public async Task EachKindGetsItsOwnSubjectCapFromConfig()
    {
        var (store, data) = Create(configure: o =>
        {
            o.Kinds[KindA] = new ClaudeKindOptions { MaxPerSubjectPerDay = 1 };
            o.Kinds[KindB] = new ClaudeKindOptions { MaxPerSubjectPerDay = 3 };
        });
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5));
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(5));
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(6));

        Assert.Equal(ClaudeBudgetCauses.PrDailyCap, (await ReserveAsync(store, KindA)).Cause);
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindB)).Kind);
    }

    [Fact]
    public async Task AKindWithoutConfig_GetsTwoRunsPerSubjectPerDay()
    {
        var (store, data) = Create();
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5));
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(6));

        Assert.Equal(ClaudeBudgetCauses.PrDailyCap, (await ReserveAsync(store, KindA)).Cause);
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindA, subject: "subject-2")).Kind);
    }

    [Fact]
    public async Task TheBreaker_OpensPerKindAndScope()
    {
        var (store, data) = Create();
        for (var i = 0; i < 3; i++)
        {
            await SeedAsync(data, KindB, TimeSpan.FromMinutes(5 + i), subject: $"failed-{i}", outcome: ClaudeRunOutcome.PodFailed);
        }

        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindA)).Kind);
        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen), await ReserveAsync(store, KindB));
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindB, scope: "scope-2")).Kind);
    }

    [Fact]
    public async Task ARevisionWithTheConfiguredFailedAttempts_GoesToTheOwner()
    {
        var (store, data) = Create(configure: o => o.Kinds[KindA] = new ClaudeKindOptions { MaxFailedAttemptsPerRevision = 1, MaxPerSubjectPerDay = 20 });
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5), outcome: ClaudeRunOutcome.TimedOut);

        Assert.Equal(ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted), await ReserveAsync(store, KindA));
        var moved = await ReserveAsync(store, KindA, revision: "rev-2");
        Assert.Equal((ClaudeReservationKind.Reserved, 1), (moved.Kind, moved.Attempt));
    }

    [Fact]
    public async Task TheAttemptNumber_IsOnePastTheFailedAttemptsOnTheRevision()
    {
        var (store, data) = Create(configure: o => o.Kinds[KindA] = new ClaudeKindOptions { MaxFailedAttemptsPerRevision = 3, MaxPerSubjectPerDay = 20 });
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5), outcome: ClaudeRunOutcome.NoResult);
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(6), outcome: ClaudeRunOutcome.Succeeded);
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(7), outcome: ClaudeRunOutcome.Cancelled);

        var reserved = await ReserveAsync(store, KindA);

        Assert.Equal((ClaudeReservationKind.Reserved, 2), (reserved.Kind, reserved.Attempt));
    }

    [Fact]
    public async Task ASubjectWithoutARevision_CountsItsFailedAttemptsTogether()
    {
        var (store, data) = Create(configure: o => o.Kinds[KindA] = new ClaudeKindOptions { MaxFailedAttemptsPerRevision = 1, MaxPerSubjectPerDay = 20 });
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5), revision: null, outcome: ClaudeRunOutcome.PodFailed);

        Assert.Equal(ClaudeBudgetCauses.AttemptsExhausted, (await ReserveAsync(store, KindA, revision: null)).Cause);
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindA, revision: "rev-1")).Kind);
    }

    [Fact]
    public async Task AnotherKindsFailedAttempts_DoNotCountOnThisKind()
    {
        var (store, data) = Create(configure: o => o.Kinds[KindA] = new ClaudeKindOptions { MaxFailedAttemptsPerRevision = 1, MaxPerSubjectPerDay = 20 });
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(5), outcome: ClaudeRunOutcome.PodFailed);

        var reserved = await ReserveAsync(store, KindA);

        Assert.Equal((ClaudeReservationKind.Reserved, 1), (reserved.Kind, reserved.Attempt));
    }

    [Fact]
    public async Task AKindsLedger_DecidesItsFailedAttempts_AndOtherKindsKeepTheirOwnCount()
    {
        var (store, _) = Create(ledgers: new FixedLedger(KindA, failed: 2));

        Assert.Equal(ClaudeBudgetCauses.AttemptsExhausted, (await ReserveAsync(store, KindA)).Cause);
        Assert.Equal(ClaudeReservationKind.Reserved, (await ReserveAsync(store, KindB)).Kind);
    }

    [Fact]
    public async Task APickedRedrive_IsBoundedByItsKindsSubjectCap()
    {
        var (store, data) = Create(configure: o =>
        {
            o.Kinds[KindA] = new ClaudeKindOptions { MaxPerSubjectPerDay = 1 };
            o.Kinds[KindB] = new ClaudeKindOptions { MaxPerSubjectPerDay = 3 };
        });
        await SeedAsync(data, KindA, TimeSpan.FromMinutes(5));
        await SeedAsync(data, KindB, TimeSpan.FromMinutes(5));

        var picked = await store.PickRedrivesAsync([new RedriveCandidate(KindA, Subject, Scope), new RedriveCandidate(KindB, Subject, Scope)], Ct);

        Assert.Equal([new RedriveCandidate(KindB, Subject, Scope)], picked);
    }

    [Theory]
    [InlineData("", Subject, Scope)]
    [InlineData(" ", Subject, Scope)]
    [InlineData(KindA, "", Scope)]
    [InlineData(KindA, Subject, " ")]
    public async Task Reserve_WithoutAKindSubjectOrScope_IsRejected(string kind, string subject, string scope)
    {
        var (store, data) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => ReserveAsync(store, kind, subject, scope));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Empty(await db.ClaudeRuns.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheReservedRun_RecordsTheKeyTheCapsCount()
    {
        var (store, data) = Create();
        var reserved = await ReserveAsync(store, KindA, revision: "rev-9");

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.ClaudeRuns.SingleAsync(r => r.Id == reserved.RunId, Ct);
        Assert.Equal((KindA, Subject, Scope, "rev-9", "wf"), (row.Kind, row.Subject, row.Scope, row.Revision, row.WorkflowId));
    }

    private sealed class FixedLedger(string kind, int failed) : IClaudeAttemptLedger
    {
        public string Kind { get; } = kind;

        public Task<int> FailedAttemptsAsync(RunDataDbContext db, ReserveClaudeRunInput input, CancellationToken cancellationToken) =>
            Task.FromResult(failed);

        public Task RecordFailureAsync(RunDataDbContext db, ClaudeRun run, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
