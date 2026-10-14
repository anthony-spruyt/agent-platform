using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class SweeperStore(IDbContextFactory<RunDataDbContext> factory, TimeProvider time)
{
    private const int SingletonId = 1;

    public async Task<SweeperState> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SweeperState.AsNoTracking().SingleOrDefaultAsync(s => s.Id == SingletonId, cancellationToken).ConfigureAwait(false)
            ?? new SweeperState { Id = SingletonId };
    }

    // Never moves back: a page read late must not make the next window re-scan what an earlier run covered.
    public Task SaveLastSeenAsync(DateTimeOffset deliveredAt, CancellationToken cancellationToken) =>
        UpdateAsync(state => state.LastSeenDeliveredAt = state.LastSeenDeliveredAt is { } seen && seen > deliveredAt ? seen : deliveredAt, cancellationToken);

    public Task SaveSuccessAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return UpdateAsync(state => state.LastSuccessAt = now, cancellationToken);
    }

    private async Task UpdateAsync(Action<SweeperState> change, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await WriteAsync(change, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(Action<SweeperState> change, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.SweeperState.SingleOrDefaultAsync(s => s.Id == SingletonId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new SweeperState { Id = SingletonId };
            db.SweeperState.Add(state);
        }

        change(state);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
