using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.RunData;

public class RunDataMigratorTests
{
    private sealed class FixedFactory(DbContextOptions<RunDataDbContext> options) : IDbContextFactory<RunDataDbContext>
    {
        public RunDataDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task RunAsync_WhenTheDatabaseIsUnreachable_ReturnsANonZeroExitCode()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2;Command Timeout=2")
            .Options;
        var migrator = new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance);

        var exitCode = await migrator.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ReturnsANonZeroExitCode()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=x").Options;
        var migrator = new RunDataMigrator(new FixedFactory(options), NullLogger<RunDataMigrator>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.NotEqual(0, await migrator.RunAsync(cancelled.Token));
    }
}
