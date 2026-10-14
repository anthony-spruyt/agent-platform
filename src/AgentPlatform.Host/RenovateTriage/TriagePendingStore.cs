using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriagePendingStore(IDbContextFactory<RunDataDbContext> factory, TimeProvider time)
{
    public async Task<TriagePending> UpsertAsync(string repo, int prNumber, string headSha, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        try
        {
            return await WriteAsync(repo, prNumber, headSha, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return await WriteAsync(repo, prNumber, headSha, reason, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TriagePending?> FindAsync(string repo, int prNumber, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TriagePending.AsNoTracking().SingleOrDefaultAsync(p => p.Repo == repo && p.PrNumber == prNumber, cancellationToken).ConfigureAwait(false);
    }

    // Only the head the caller worked on: after a head move the row belongs to the new head and a late writer must not settle it.
    public async Task<bool> SetStatusAsync(string repo, int prNumber, string headSha, TriagePendingStatus status, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriagePending
            .SingleOrDefaultAsync(p => p.Repo == repo && p.PrNumber == prNumber && p.HeadSha == headSha, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        row.Status = status;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A decided head leaves no reason to wait on an earlier one, so an open row of another head would only inflate the pending count.
    public async Task SettleOtherHeadsAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriagePending
            .SingleOrDefaultAsync(p => p.Repo == repo && p.PrNumber == prNumber && p.HeadSha != headSha && (p.Status == TriagePendingStatus.Pending || p.Status == TriagePendingStatus.Redriven), cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Status = TriagePendingStatus.Settled;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountOpenAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TriagePending
            .CountAsync(p => p.Status == TriagePendingStatus.Pending || p.Status == TriagePendingStatus.Redriven, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TriagePending> WriteAsync(string repo, int prNumber, string headSha, string reason, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var row = await db.TriagePending.SingleOrDefaultAsync(p => p.Repo == repo && p.PrNumber == prNumber, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new TriagePending
            {
                Id = Guid.NewGuid(),
                Repo = repo,
                PrNumber = prNumber,
                HeadSha = headSha,
                Reason = reason,
                Status = TriagePendingStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.TriagePending.Add(row);
        }
        else if (row.HeadSha != headSha)
        {
            row.HeadSha = headSha;
            row.Reason = reason;
            row.Status = TriagePendingStatus.Pending;
            row.Attempts = 0;
            row.UpdatedAt = now;
        }
        else
        {
            row.Reason = reason;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Entry(row).State = EntityState.Detached;
        return row;
    }
}
