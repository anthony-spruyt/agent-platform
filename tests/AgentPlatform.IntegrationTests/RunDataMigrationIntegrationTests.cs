using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.IntegrationTests;

[Collection(AppHostSharedFixture.Name)]
public class RunDataMigrationIntegrationTests(AppHostFixture fixture)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private sealed class FixedFactory(DbContextOptions<RunDataDbContext> options) : IDbContextFactory<RunDataDbContext>
    {
        public RunDataDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task MigrateRole_FinishesWithExitCodeZeroBeforeTheWorkerStarts()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);

        await fixture.App.ResourceNotifications.WaitForResourceAsync("migrate", KnownResourceStates.Finished, timeout.Token);

        Assert.True(fixture.App.ResourceNotifications.TryGetCurrentState("migrate", out var migrate));
        Assert.Equal(0, migrate!.Snapshot.ExitCode);
    }

    [Fact]
    public async Task Migrations_CreateTheRunDataTablesAndRecordTheMigration()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var options = await EmptyDatabaseOptionsAsync(ct);
        await new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance).RunAsync(ct);

        await using var db = new RunDataDbContext(options);
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync(ct);
        Assert.Contains("prompts", tables);
        Assert.Contains("runs", tables);
        Assert.Contains("run_steps", tables);
        Assert.Contains("renovate_triage_verdicts", tables);
        Assert.Contains("claude_runs", tables);
        Assert.Contains("renovate_triage_pending", tables);
        Assert.Contains("claude_cooldowns", tables);
        Assert.Contains("sweeper_state", tables);
        Assert.Contains("__EFMigrationsHistory", tables);
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(ct));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
    }

    [Fact]
    public async Task Migrate_RunTwice_SucceedsBothTimesAndChangesNothing()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var options = await EmptyDatabaseOptionsAsync(ct);
        var migrator = new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance);

        var first = await migrator.RunAsync(ct);
        var appliedAfterFirst = await AppliedMigrationsAsync(options, ct);
        var second = await migrator.RunAsync(ct);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(appliedAfterFirst, await AppliedMigrationsAsync(options, ct));
    }

    [Fact]
    public async Task Migrate_RunConcurrently_SucceedsForEveryRunner()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var options = await EmptyDatabaseOptionsAsync(ct);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance).RunAsync(ct)));

        Assert.All(results, result => Assert.Equal(0, result));
        Assert.Empty(await PendingMigrationsAsync(options, ct));
    }

    private async Task<DbContextOptions<RunDataDbContext>> EmptyDatabaseOptionsAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(await fixture.CreateEmptyDatabaseAsync(ct)).Options;
        Assert.Empty(await AppliedMigrationsAsync(options, ct));
        return options;
    }

    private static async Task<IReadOnlyList<string>> AppliedMigrationsAsync(DbContextOptions<RunDataDbContext> options, CancellationToken ct)
    {
        await using var db = new RunDataDbContext(options);
        return (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
    }

    private static async Task<IReadOnlyList<string>> PendingMigrationsAsync(DbContextOptions<RunDataDbContext> options, CancellationToken ct)
    {
        await using var db = new RunDataDbContext(options);
        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }
}
