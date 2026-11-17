using System.Text.Json;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record TriageWait(string Repo, int PrNumber, string HeadSha, TriageDecision Decision);

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

        var reason = ReasonOf(row);
        return TriageRules.IsTransient(reason) ? null : new TriageDecision(row.Verdict, reason, row.Comment);
    }

    public async Task<bool> IsJudgedAsync(string repo, int prNumber, string headSha, TriageMode mode, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TriageVerdicts
            .AsNoTracking()
            .Where(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == mode && (mode != TriageMode.Live || v.AppliedAt != null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Any(r => mode != TriageMode.Live || !TriageRules.IsTransient(ReasonOf(r)));
    }

    // Written before the hand-off's GitHub writes, so a stored approval never outlives them unapplied.
    public Task<bool> MarkTransientAsync(string repo, int prNumber, string headSha, TriageDecision decision, CancellationToken cancellationToken) =>
        OverwriteAsync(repo, prNumber, headSha, decision, row => row.AppliedAt is null || TriageRules.IsTransient(ReasonOf(row)), cancellationToken);

    public async Task<IReadOnlyList<TriageWait>> ListWaitsAsync(DateTimeOffset createdFrom, DateTimeOffset createdTo, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TriageVerdicts
            .AsNoTracking()
            .Where(v => v.Mode == TriageMode.Live && v.CreatedAt >= createdFrom && v.CreatedAt <= createdTo && v.Comment != null && v.Comment.StartsWith(TriageRules.WaitingPrefix))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Where(IsWait).Select(r => new TriageWait(r.Repo, r.PrNumber, r.HeadSha, new TriageDecision(r.Verdict, ReasonOf(r), r.Comment)))];
    }

    public async Task EndWaitAsync(string repo, int prNumber, string headSha, TriageDecision handOff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handOff);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriageVerdicts
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == TriageMode.Live, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || !IsWait(row))
        {
            return;
        }

        row.Comment = handOff.Comment;
        row.Action = VerdictAction.Comment;
        row.AppliedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Only the row of the verdict that was written counts: a flag written over an unapplied approval leaves that approval unapplied.
    public async Task MarkAppliedAsync(string repo, int prNumber, string headSha, TriageVerdict verdict, VerdictAction action, CancellationToken cancellationToken)
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

    // The row keeps what Apply actually wrote, so a downgrade or hand-off is not recorded as the verdict the agent gave.
    public async Task MarkAppliedAsAsync(string repo, int prNumber, string headSha, TriageVerdict recorded, TriageDecision effective, VerdictAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriageVerdicts
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == TriageMode.Live && v.Verdict == recorded && v.AppliedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Verdict = effective.Verdict;
        row.Reasons = JsonSerializer.Serialize(new[] { effective.Reason });
        row.Comment = effective.Comment;
        row.AppliedAt = DateTimeOffset.UtcNow;
        row.Action = action;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // The row must stay applied: Live counts only applied rows as judged.
    public Task WithdrawAsync(string repo, int prNumber, string headSha, TriageDecision decision, CancellationToken cancellationToken) =>
        OverwriteAsync(repo, prNumber, headSha, decision, _ => true, cancellationToken);

    private async Task<bool> OverwriteAsync(string repo, int prNumber, string headSha, TriageDecision decision, Func<RenovateTriageVerdict, bool> replaces, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentNullException.ThrowIfNull(decision);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TriageVerdicts
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == TriageMode.Live, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new RenovateTriageVerdict { Id = Guid.NewGuid(), Repo = repo, PrNumber = prNumber, HeadSha = headSha, Reasons = string.Empty, Mode = TriageMode.Live };
            db.TriageVerdicts.Add(row);
        }
        else if (!replaces(row))
        {
            return false;
        }

        row.Verdict = decision.Verdict;
        row.Reasons = JsonSerializer.Serialize(new[] { decision.Reason });
        row.Comment = decision.Comment;
        row.Rung = LadderRung.Rule;
        row.Action = VerdictAction.Comment;
        row.AppliedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string ReasonOf(RenovateTriageVerdict row) => JsonSerializer.Deserialize<string[]>(row.Reasons)?.FirstOrDefault() ?? string.Empty;

    private static bool IsWait(RenovateTriageVerdict row) =>
        row.Mode == TriageMode.Live && TriageRules.IsTransient(ReasonOf(row)) && row.Comment?.StartsWith(TriageRules.WaitingPrefix, StringComparison.Ordinal) == true;

    // The first verdict for a commit stands; a concurrent writer can win the unique key, so retry once on a fresh context.
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

    private async Task<bool> InsertAsync(string repo, int prNumber, string headSha, TriageDecision decision, TriageMode mode, LadderRung? rung, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.TriageVerdicts
            .SingleOrDefaultAsync(v => v.Repo == repo && v.PrNumber == prNumber && v.HeadSha == headSha && v.Mode == mode, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (mode != TriageMode.Live || !TriageRules.IsTransient(ReasonOf(existing)))
            {
                return false;
            }

            existing.Verdict = decision.Verdict;
            existing.Reasons = JsonSerializer.Serialize(new[] { decision.Reason });
            existing.Comment = decision.Comment;
            existing.Rung = rung;
            existing.AppliedAt = null;
            existing.Action = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
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
