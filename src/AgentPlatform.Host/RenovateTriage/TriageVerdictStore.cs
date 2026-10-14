using System.Text.Json;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageVerdictStore(IDbContextFactory<RunDataDbContext> factory)
{
    // Live only counts a verdict as judged once its GitHub writes are done; Shadow has none.
    public async Task<bool> ExistsAsync(string repo, int prNumber, string headSha, TriageMode mode, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TriageVerdicts
            .AnyAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == mode && (mode != TriageMode.Live || v.AppliedAt != null), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TriageDecision?> FindUnappliedAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriageVerdicts
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == TriageMode.Live && v.AppliedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var reason = JsonSerializer.Deserialize<string[]>(row.Reasons)?.FirstOrDefault() ?? string.Empty;
        return new TriageDecision(row.Verdict, reason, row.Comment);
    }

    // Only the row of the verdict that was written counts: a flag written over an unapplied approval leaves that approval unapplied.
    public Task MarkAppliedAsync(string repo, int prNumber, string headSha, TriageVerdict verdict, CancellationToken cancellationToken) =>
        MarkAppliedAsync(repo, prNumber, headSha, verdict, null, cancellationToken);

    public async Task MarkAppliedAsync(string repo, int prNumber, string headSha, TriageVerdict verdict, VerdictAction? action, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriageVerdicts
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == TriageMode.Live && v.Verdict == verdict && v.AppliedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.AppliedAt = DateTimeOffset.UtcNow;
        row.Action = action;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // The first verdict for a commit stands; a concurrent writer can win the unique key, so retry once on a fresh context.
    public Task<bool> RecordAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, CancellationToken cancellationToken) =>
        RecordAsync(repo, prNumber, headSha, decision, mode, null, cancellationToken);

    public async Task<bool> RecordAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, LadderRung? rung, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            return await InsertAsync(repo, prNumber, headSha, decision, mode, rung, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return await InsertAsync(repo, prNumber, headSha, decision, mode, rung, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> RowExistsAsync(RunDataDbContext db, string repo, int prNumber, string headSha, TriageMode mode, CancellationToken cancellationToken) =>
        db.TriageVerdicts.AnyAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == mode, cancellationToken);

    private async Task<bool> InsertAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, LadderRung? rung, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await RowExistsAsync(db, repo, prNumber, headSha, mode, cancellationToken).ConfigureAwait(false))
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
            Rung = rung,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
