using System.Text.Json;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageVerdictStore(IDbContextFactory<RunDataDbContext> factory)
{
    public async Task<bool> ExistsAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ExistsAsync(db, repo, prNumber, headSha, cancellationToken).ConfigureAwait(false);
    }

    // The first verdict for a commit stands; a concurrent writer can win the unique key, so retry once on a fresh context.
    public async Task<bool> RecordAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            return await InsertAsync(repo, prNumber, headSha, decision, mode, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return await InsertAsync(repo, prNumber, headSha, decision, mode, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> ExistsAsync(RunDataDbContext db, string repo, int prNumber, string headSha, CancellationToken cancellationToken) =>
        db.TriageVerdicts.AnyAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha, cancellationToken);

    private async Task<bool> InsertAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await ExistsAsync(db, repo, prNumber, headSha, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        db.TriageVerdicts.Add(new RenovateTriageVerdict
        {
            Id = Guid.NewGuid(),
            Repo = repo,
            PrNumber = prNumber,
            HeadSha = headSha,
            Verdict = decision.Verdict,
            Reasons = JsonSerializer.Serialize(new[] { decision.Reason }),
            Comment = decision.Comment,
            Mode = mode,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
