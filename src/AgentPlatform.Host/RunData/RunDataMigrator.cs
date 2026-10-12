using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RunData;

public sealed partial class RunDataMigrator(IDbContextFactory<RunDataDbContext> factory, ILogger<RunDataMigrator> logger)
{
    private const long MigrationLockKey = 7_319_204_551;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Session lock on a pooled connection, released explicitly; EF's own lock still let a second runner re-apply a migration.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({MigrationLockKey})", cancellationToken).ConfigureAwait(false);
            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
                LogApplying(logger, pending);
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                LogApplied(logger);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({MigrationLockKey})", CancellationToken.None).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Pending} pending run-data migrations.")]
    private static partial void LogApplying(ILogger logger, int pending);

    [LoggerMessage(Level = LogLevel.Information, Message = "The run-data database is up to date.")]
    private static partial void LogApplied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Applying the run-data migrations failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
