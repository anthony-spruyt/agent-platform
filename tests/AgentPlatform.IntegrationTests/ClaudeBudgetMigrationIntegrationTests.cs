using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace AgentPlatform.IntegrationTests;

[Collection(AppHostSharedFixture.Name)]
public class ClaudeBudgetMigrationIntegrationTests(AppHostFixture fixture)
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Revision = "rev-one";
    private const string MigrationName = "_GeneralizeClaudeBudgetKey";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset Reserved = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);

    private sealed class OldRow
    {
        public string Repo { get; set; } = string.Empty;

        public int PrNumber { get; set; }

        public string HeadSha { get; set; } = string.Empty;

        public string? Outcome { get; set; }
    }

    private sealed class LegacyRow
    {
        public string? Repo { get; init; }

        public int? PrNumber { get; init; }

        public string? HeadSha { get; init; }
    }

    private sealed class FixedFactory(DbContextOptions<RunDataDbContext> options) : IDbContextFactory<RunDataDbContext>
    {
        public RunDataDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seeded(Guid Finished, Guid Open);

    private static ClaudeBudgetStore StoreOver(RunDataDbContext db, DateTimeOffset now, int maxPerSubjectPerDay)
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(db.Database.GetConnectionString()!).Options;
        var caps = new ClaudeBudgetOptions { Kinds = { [RenovateBudget.Kind] = new ClaudeKindOptions { MaxPerSubjectPerDay = maxPerSubjectPerDay } } };
        return new ClaudeBudgetStore(new FixedFactory(options), Options.Create(caps), new FixedTime(now), [new TriageAttemptLedger()]);
    }

    private static Task<LegacyRow> LegacyColumnsAsync(RunDataDbContext db, Guid id, CancellationToken ct) =>
        db.Database.SqlQuery<LegacyRow>($"SELECT repo AS \"Repo\", pr_number AS \"PrNumber\", head_sha AS \"HeadSha\" FROM claude_runs WHERE id = {id}").SingleAsync(ct);

    private static string BeforeTheKeyMigration(RunDataDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        return all[all.FindIndex(m => m.EndsWith(MigrationName, StringComparison.Ordinal)) - 1];
    }

    private static async Task<Seeded> SeedOldShapeAsync(RunDataDbContext db, CancellationToken ct)
    {
        var finished = Guid.NewGuid();
        var open = Guid.NewGuid();
        var podCreated = Reserved.AddSeconds(5);
        var finishedAt = Reserved.AddMinutes(3);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO claude_runs (id, repo, pr_number, head_sha, workflow_id, attempt, reserved_at, pod_created_at, finished_at, outcome, model, input_tokens, output_tokens, cost_usd)
            VALUES ({finished}, {Repo}, {7}, {Revision}, {"wf-7"}, {2}, {Reserved}, {podCreated}, {finishedAt}, {"TimedOut"}, {"opus"}, {120L}, {30L}, {0.42m})
            """,
            ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO claude_runs (id, repo, pr_number, head_sha, workflow_id, attempt, reserved_at)
            VALUES ({open}, {"anthony-spruyt/other"}, {1234}, {"rev-two"}, {"wf-1234"}, {1}, {Reserved})
            """,
            ct);
        return new Seeded(finished, open);
    }

    private async Task<(RunDataDbContext Db, IMigrator Migrator)> OpenAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(await fixture.CreateEmptyDatabaseAsync(ct)).Options;
        var db = new RunDataDbContext(options);
        return (db, db.GetService<IMigrator>());
    }

    private static CancellationTokenSource Timeout(out CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TestTimeout);
        ct = source.Token;
        return source;
    }

    [Fact]
    public async Task Up_MovesEveryExistingRowToTheRenovateKind_WithoutLoss()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        await migrator.MigrateAsync(BeforeTheKeyMigration(db), ct);
        var seeded = await SeedOldShapeAsync(db, ct);

        await migrator.MigrateAsync(null, ct);

        var rows = await db.ClaudeRuns.AsNoTracking().OrderBy(r => r.Attempt).ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        var open = rows[0];
        var finished = rows[1];
        Assert.Equal((seeded.Open, RenovateBudget.Kind, "anthony-spruyt/other#1234", "anthony-spruyt/other", "rev-two"), (open.Id, open.Kind, open.Subject, open.Scope, open.Revision));
        Assert.Equal((seeded.Finished, RenovateBudget.Kind, $"{Repo}#7", Repo, Revision), (finished.Id, finished.Kind, finished.Subject, finished.Scope, finished.Revision));
        Assert.Equal(("wf-7", 2, Reserved, Reserved.AddSeconds(5), Reserved.AddMinutes(3)), (finished.WorkflowId, finished.Attempt, finished.ReservedAt, finished.PodCreatedAt, finished.FinishedAt));
        Assert.Equal((ClaudeRunOutcome.TimedOut, "opus", 120L, 30L, 0.42m), (finished.Outcome, finished.Model, finished.InputTokens, finished.OutputTokens, finished.CostUsd));
        Assert.Equal((null, null, null), (open.PodCreatedAt, open.FinishedAt, open.Outcome));
    }

    [Fact]
    public async Task Down_RestoresTheOldColumns_AndKeepsOnlyTheRenovateRuns()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        var before = BeforeTheKeyMigration(db);
        await migrator.MigrateAsync(before, ct);
        await SeedOldShapeAsync(db, ct);
        await migrator.MigrateAsync(null, ct);
        db.ClaudeRuns.Add(new ClaudeRun { Id = Guid.NewGuid(), Kind = "other-kind", Subject = "thing", Scope = "thing", WorkflowId = "wf-x", Attempt = 1, ReservedAt = Reserved });
        await db.SaveChangesAsync(ct);

        await migrator.MigrateAsync(before, ct);

        var rows = await db.Database
            .SqlQuery<OldRow>($"SELECT repo AS \"Repo\", pr_number AS \"PrNumber\", head_sha AS \"HeadSha\", outcome AS \"Outcome\" FROM claude_runs ORDER BY pr_number")
            .ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("anthony-spruyt/widgets", 7, Revision, "TimedOut"), (rows[0].Repo, rows[0].PrNumber, rows[0].HeadSha, rows[0].Outcome));
        Assert.Equal(("anthony-spruyt/other", 1234, "rev-two", null), (rows[1].Repo, rows[1].PrNumber, rows[1].HeadSha, rows[1].Outcome));
    }

    [Fact]
    public async Task UpDownUp_ReturnsTheSameRows()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        var before = BeforeTheKeyMigration(db);
        await migrator.MigrateAsync(before, ct);
        await SeedOldShapeAsync(db, ct);
        await migrator.MigrateAsync(null, ct);
        var first = await db.ClaudeRuns.AsNoTracking().OrderBy(r => r.Attempt).Select(r => new { r.Id, r.Kind, r.Subject, r.Scope, r.Revision }).ToListAsync(ct);

        await migrator.MigrateAsync(before, ct);
        await migrator.MigrateAsync(null, ct);

        Assert.Equal(first, await db.ClaudeRuns.AsNoTracking().OrderBy(r => r.Attempt).Select(r => new { r.Id, r.Kind, r.Subject, r.Scope, r.Revision }).ToListAsync(ct));
    }

    [Fact]
    public async Task Up_KeepsTheOldColumnsFilled_SoTheImageBeforeTheMigrationCanStillReadThem()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        await migrator.MigrateAsync(BeforeTheKeyMigration(db), ct);
        var seeded = await SeedOldShapeAsync(db, ct);

        await migrator.MigrateAsync(null, ct);

        var finished = await LegacyColumnsAsync(db, seeded.Finished, ct);
        var open = await LegacyColumnsAsync(db, seeded.Open, ct);
        Assert.Equal((Repo, 7, Revision), (finished.Repo, finished.PrNumber, finished.HeadSha));
        Assert.Equal(("anthony-spruyt/other", 1234, "rev-two"), (open.Repo, open.PrNumber, open.HeadSha));
    }

    [Fact]
    public async Task ARowInsertedWithOnlyTheOldColumns_AfterTheMigration_IsAcceptedAndCountedByTheNewStore()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        await migrator.MigrateAsync(null, ct);
        var oldImageRow = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO claude_runs (id, repo, pr_number, head_sha, workflow_id, attempt, reserved_at)
            VALUES ({oldImageRow}, {Repo}, {7}, {Revision}, {"wf-7"}, {1}, {Reserved})
            """,
            ct);

        var row = await db.ClaudeRuns.AsNoTracking().SingleAsync(r => r.Id == oldImageRow, ct);
        var reservation = await StoreOver(db, Reserved.AddMinutes(30), maxPerSubjectPerDay: 1)
            .ReserveAsync("wf-7", RenovateBudget.Reserve(Guid.NewGuid(), Repo, 7, Revision, "opus"), ct);

        Assert.Equal((RenovateBudget.Kind, $"{Repo}#7", Repo, Revision), (row.Kind, row.Subject, row.Scope, row.Revision));
        Assert.Equal((ClaudeReservationKind.Park, ClaudeBudgetCauses.PrDailyCap), (reservation.Kind, reservation.Cause));
    }

    [Fact]
    public async Task Down_RebuildsTheOldColumnsOfARenovateRunThatLacksThem()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        var before = BeforeTheKeyMigration(db);
        await migrator.MigrateAsync(null, ct);
        var runId = Guid.NewGuid();
        db.ClaudeRuns.Add(new ClaudeRun { Id = runId, Kind = RenovateBudget.Kind, Subject = $"{Repo}#31", Scope = Repo, Revision = Revision, WorkflowId = "wf-31", Attempt = 1, ReservedAt = Reserved });
        await db.SaveChangesAsync(ct);

        await migrator.MigrateAsync(before, ct);

        var row = await db.Database
            .SqlQuery<OldRow>($"SELECT repo AS \"Repo\", pr_number AS \"PrNumber\", head_sha AS \"HeadSha\", outcome AS \"Outcome\" FROM claude_runs WHERE id = {runId}")
            .SingleAsync(ct);
        Assert.Equal((Repo, 31, Revision), (row.Repo, row.PrNumber, row.HeadSha));
    }

    [Fact]
    public async Task ARunFromTheNewStore_FillsTheOldColumnsForRenovate_AndLeavesThemEmptyForAnotherKind()
    {
        using var _ = Timeout(out var ct);
        var (db, migrator) = await OpenAsync(ct);
        await using var __ = db;
        await migrator.MigrateAsync(null, ct);
        var store = StoreOver(db, Reserved, maxPerSubjectPerDay: 2);

        var renovate = await store.ReserveAsync("wf-7", RenovateBudget.Reserve(Guid.NewGuid(), Repo, 7, Revision, "opus"), ct);
        var other = await store.ReserveAsync("wf-x", new ReserveClaudeRunInput(Guid.NewGuid(), "other-kind", "thing-1", "thing", "rev-1", "opus"), ct);

        var renovateRow = await LegacyColumnsAsync(db, renovate.RunId!.Value, ct);
        var otherRow = await LegacyColumnsAsync(db, other.RunId!.Value, ct);
        Assert.Equal((Repo, 7, Revision), (renovateRow.Repo, renovateRow.PrNumber, renovateRow.HeadSha));
        Assert.Equal((null, null, null), (otherRow.Repo, otherRow.PrNumber, otherRow.HeadSha));
    }
}
