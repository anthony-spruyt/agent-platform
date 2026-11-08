using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.Livemap;

public sealed class LivemapBuildStore(IDbContextFactory<RunDataDbContext> factory, TimeProvider time)
{
    public const int MaxDetailChars = 1000;

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

    // The build in flight is the one a workflow is working on; a parked build waits for its retry time and has no workflow.
    public async Task<LivemapBuild?> FindActiveAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.LivemapBuilds.AsNoTracking()
            .Where(b => b.State == LivemapBuildState.Detected)
            .OrderByDescending(b => b.BuildId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LivemapBuild>> ListDueAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.LivemapBuilds.AsNoTracking()
            .Where(b => b.State == LivemapBuildState.Parked && b.NextRetryAt <= now)
            .OrderBy(b => b.BuildId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ClassifyAsync(long buildId, LivemapClassification classification, long compatRunId, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Detected)
                {
                    return null;
                }

                build.Classification = classification;
                build.CompatRunId = compatRunId;
                return LivemapBuildState.Classified;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> FlagForOwnerAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Detected)
                {
                    return null;
                }

                build.Detail = Truncate(detail);
                return LivemapBuildState.NeedsOwner;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<LivemapBuildState?> ParkAsync(long buildId, string detail, int maxAttempts, TimeSpan retryAfter, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, now) =>
            {
                if (build.State != LivemapBuildState.Detected)
                {
                    return null;
                }

                build.Attempts++;
                build.Detail = Truncate(detail);
                if (build.Attempts >= maxAttempts)
                {
                    return LivemapBuildState.NeedsOwner;
                }

                build.NextRetryAt = now + retryAfter;
                return LivemapBuildState.Parked;
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<bool> ResumeAsync(long buildId, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, now) =>
            {
                if (build.State != LivemapBuildState.Parked || build.NextRetryAt is not { } retryAt || retryAt > now)
                {
                    return null;
                }

                return LivemapBuildState.Detected;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<int> SupersedeOlderAsync(long buildId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var older = await db.LivemapBuilds
                .Where(b => b.BuildId < buildId
                    && (b.State == LivemapBuildState.Detected || b.State == LivemapBuildState.Parked || b.State == LivemapBuildState.Classified))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var build in older)
            {
                build.State = LivemapBuildState.Superseded;
                build.UpdatedAt = now;
                build.Version++;
            }

            if (await TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
            {
                return older.Count;
            }
        }

        throw new DbUpdateConcurrencyException("Other writers keep moving the older builds.");
    }

    private static async Task<bool> TrySaveAsync(RunDataDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    // Each transition starts from one state only, and the version makes a stale writer lose, so a workflow whose build a newer one replaced changes nothing.
    private async Task<LivemapBuildState?> MoveAsync(long buildId, Func<LivemapBuild, DateTimeOffset, LivemapBuildState?> change, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var build = await db.LivemapBuilds.SingleOrDefaultAsync(b => b.BuildId == buildId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (build is null || change(build, now) is not { } next)
        {
            return null;
        }

        build.State = next;
        build.UpdatedAt = now;
        build.Version++;
        return await TrySaveAsync(db, cancellationToken).ConfigureAwait(false) ? next : null;
    }

    private static string Truncate(string detail) => detail.Length > MaxDetailChars ? detail[..MaxDetailChars] : detail;

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
