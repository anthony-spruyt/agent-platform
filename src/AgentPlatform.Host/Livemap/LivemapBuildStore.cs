using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.Livemap;

public sealed class LivemapBuildStore(IDbContextFactory<RunDataDbContext> factory, TimeProvider time)
{
    public async Task<LivemapBuild?> FindAsync(long buildId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LivemapBuilds.AsNoTracking().SingleOrDefaultAsync(b => b.BuildId == buildId, cancellationToken).ConfigureAwait(false);
    }

    // First writer wins: an existing row is returned untouched, so a repeated poll never rewinds a build's state.
    public async Task<LivemapBuild> RecordAsync(long buildId, string manifestGid, DateTimeOffset steamUpdatedAt, LivemapBuildState state, CancellationToken cancellationToken)
    {
        try
        {
            return await InsertIfAbsentAsync(buildId, manifestGid, steamUpdatedAt, state, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return await InsertIfAbsentAsync(buildId, manifestGid, steamUpdatedAt, state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LivemapBuild> InsertIfAbsentAsync(long buildId, string manifestGid, DateTimeOffset steamUpdatedAt, LivemapBuildState state, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.LivemapBuilds.AsNoTracking().SingleOrDefaultAsync(b => b.BuildId == buildId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = time.GetUtcNow();
        var build = new LivemapBuild
        {
            BuildId = buildId,
            ManifestGid = manifestGid,
            SteamUpdatedAt = steamUpdatedAt,
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.LivemapBuilds.Add(build);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return build;
    }
}
