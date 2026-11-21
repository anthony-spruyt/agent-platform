using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageRebaseStore(IDbContextFactory<RunDataDbContext> factory, TimeProvider time)
{
    public async Task<TriageRebase?> FindAsync(string repo, int prNumber, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TriageRebases.AsNoTracking().SingleOrDefaultAsync(r => r.Repo == repo && r.PrNumber == prNumber, cancellationToken).ConfigureAwait(false);
    }

    // The first record wins: it is the one rebase a pull request gets, and a later head must not move it.
    public async Task RecordAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        try
        {
            await WriteAsync(repo, prNumber, headSha, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await WriteAsync(repo, prNumber, headSha, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.TriageRebases.AnyAsync(r => r.Repo == repo && r.PrNumber == prNumber, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.TriageRebases.Add(new TriageRebase { Id = Guid.NewGuid(), Repo = repo, PrNumber = prNumber, HeadSha = headSha, RequestedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
