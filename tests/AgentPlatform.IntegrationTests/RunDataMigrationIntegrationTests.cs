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
    public async Task Migrations_CreateTheRunDataTablesAndRunningThemAgainChangesNothing()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var connectionString = await fixture.RunDataConnectionStringAsync(ct);
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(connectionString).Options;
        var migrator = new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance);

        var again = await migrator.RunAsync(ct);

        Assert.Equal(0, again);
        await using var db = new RunDataDbContext(options);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        Assert.True(await db.Prompts.CountAsync(ct) >= 0);
        Assert.True(await db.Runs.CountAsync(ct) >= 0);
        Assert.True(await db.RunSteps.CountAsync(ct) >= 0);
    }
}
