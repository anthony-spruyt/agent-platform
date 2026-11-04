using System.Data;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AgentPlatform.Host.ClaudeBudget;

public sealed class UnknownClaudeRunException(Guid runId) : InvalidOperationException($"Claude run {runId} does not exist.");

public sealed class ClaudeBudgetStore(IDbContextFactory<RunDataDbContext> factory, IOptions<ClaudeBudgetOptions> options, TimeProvider time, IEnumerable<IClaudeAttemptLedger> ledgers)
{
    // An open reservation that never reached MarkPodCreated or Finish (a crashed workflow) counts against the caps this long.
    public static readonly TimeSpan OpenReservationLease = TimeSpan.FromHours(2);

    private const int BreakerThreshold = 3;
    private const int MaxTransactionTries = 5;
    private const string SerializationFailure = "40001";
    private const string DeadlockDetected = "40P01";
    private const string UniqueViolation = "23505";

    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan _day = TimeSpan.FromHours(24);
    private static readonly TimeSpan _breakerOpenFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan _minCooldown = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, IClaudeAttemptLedger> _ledgers = ledgers.ToDictionary(l => l.Kind, StringComparer.Ordinal);

    // One serializable transaction decides, so concurrent reservations can't both take the last slot.
    public async Task<ClaudeReservation> ReserveAsync(string workflowId, ReserveClaudeRunInput input, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Model);
        if (input.RunId == Guid.Empty)
        {
            throw new ArgumentException("RunId is required.", nameof(input));
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryReserveAsync(workflowId, input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxTransactionTries && IsRetryable(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 120)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task MarkPodCreatedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var updated = await db.ClaudeRuns
            .Where(r => r.Id == runId && r.PodCreatedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PodCreatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0 && !await db.ClaudeRuns.AnyAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false))
        {
            throw new UnknownClaudeRunException(runId);
        }
    }

    // The first outcome stands; any outcome but NotStarted counts the run even if MarkPodCreated never ran.
    public async Task FinishAsync(FinishClaudeRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var model = input.Model;
        var outcome = (ClaudeRunOutcome?)input.Outcome;
        var podWasCreated = input.Outcome != ClaudeRunOutcome.NotStarted;
        var updated = await db.ClaudeRuns
            .Where(r => r.Id == input.RunId && r.FinishedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.PodCreatedAt, r => podWasCreated ? r.PodCreatedAt ?? r.ReservedAt : r.PodCreatedAt)
                    .SetProperty(r => r.Outcome, outcome)
                    .SetProperty(r => r.Model, r => model ?? r.Model)
                    .SetProperty(r => r.InputTokens, input.InputTokens)
                    .SetProperty(r => r.OutputTokens, input.OutputTokens)
                    .SetProperty(r => r.CostUsd, input.CostUsd),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            if (!await db.ClaudeRuns.AnyAsync(r => r.Id == input.RunId, cancellationToken).ConfigureAwait(false))
            {
                throw new UnknownClaudeRunException(input.RunId);
            }

            return;
        }

        if (input.Outcome.IsFailure())
        {
            var run = await db.ClaudeRuns.AsNoTracking().SingleAsync(r => r.Id == input.RunId, cancellationToken).ConfigureAwait(false);
            if (_ledgers.TryGetValue(run.Kind, out var ledger))
            {
                await ledger.RecordFailureAsync(db, run, now, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Keeps the later end, so a second limit result never shortens a cooldown that is already running; returns the end now in force.
    public async Task<DateTimeOffset> SetCooldownAsync(SetClaudeCooldownInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Cause);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var until = input.ResetsAt is { } resetsAt ? Clamp(resetsAt.ToUniversalTime(), now + _minCooldown, now + _day) : now + _hour;
        var scope = input.Scope;
        var cause = input.Cause;
        var effective = await db.Database.SqlQuery<DateTimeOffset>(
            $"""
            INSERT INTO claude_cooldowns (scope, until, cause) VALUES ({scope}, {until}, {cause})
            ON CONFLICT (scope) DO UPDATE SET
                cause = CASE WHEN EXCLUDED.until > claude_cooldowns.until THEN EXCLUDED.cause ELSE claude_cooldowns.cause END,
                until = GREATEST(claude_cooldowns.until, EXCLUDED.until)
            RETURNING until AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return effective.Single();
    }

    // Plain reads, no reservation: the re-driven workflow still reserves for itself, so this only stops the sweeper from waking runs that would park again.
    public async Task<IReadOnlyList<RedriveCandidate>> PickRedrivesAsync(IReadOnlyList<RedriveCandidate> candidates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var cooldowns = await db.ClaudeCooldowns.AsNoTracking().Where(c => c.Until > now).Select(c => c.Scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (cooldowns.Contains(ClaudeCooldown.Global, StringComparer.Ordinal))
        {
            return [];
        }

        var caps = options.Value;
        var counted = await CountedRunsAsync(db, now, cancellationToken).ConfigureAwait(false);
        var slots = Math.Min(caps.MaxPerHour - counted.Count(r => r.At > now - _hour), caps.MaxPerDay - counted.Count);
        var picked = new List<RedriveCandidate>();
        // Exempt candidates go first and ignore the slot count, but each still uses one.
        foreach (var candidate in candidates.Where(c => c.ExemptFromGlobalCaps).Concat(candidates.Where(c => !c.ExemptFromGlobalCaps)))
        {
            if (!candidate.ExemptFromGlobalCaps && picked.Count >= slots)
            {
                break;
            }

            if (cooldowns.Contains(candidate.Scope, StringComparer.Ordinal)
                || counted.Count(r => r.Kind == candidate.Kind && r.Subject == candidate.Subject) >= caps.For(candidate.Kind).MaxPerSubjectPerDay
                || await BreakerOpenAsync(db, candidate.Kind, candidate.Scope, now, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            picked.Add(candidate);
        }

        return picked;
    }

    public async Task<int> CountActiveCooldownsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.ClaudeCooldowns.CountAsync(c => c.Until > now, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRetryable(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: SerializationFailure or DeadlockDetected or UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max) => value < min ? min : value > max ? max : value;

    private static DateTimeOffset? FreeAt(IEnumerable<CountedRun> runs, int cap, TimeSpan window)
    {
        var leavesAt = runs.Select(r => r.At + (r.Open ? Min(window, OpenReservationLease) : window)).Order().ToList();
        return leavesAt.Count >= cap ? leavesAt[leavesAt.Count - cap] : null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private async Task<ClaudeReservation> TryReserveAsync(string workflowId, ReserveClaudeRunInput input, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var existing = await db.ClaudeRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == input.RunId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ClaudeReservation.Reserved(existing.Id, existing.Attempt);
        }

        var now = time.GetUtcNow();
        var cooldown = await db.ClaudeCooldowns
            .AsNoTracking()
            .Where(c => c.Until > now && (c.Scope == ClaudeCooldown.Global || c.Scope == input.Scope))
            .OrderByDescending(c => c.Until)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cooldown is not null)
        {
            return ClaudeReservation.Park(cooldown.Cause, cooldown.Until);
        }

        if (await CapBlockAsync(db, input, now, cancellationToken).ConfigureAwait(false) is { } block)
        {
            return block;
        }

        if (await BreakerOpenAsync(db, input.Kind, input.Scope, now, cancellationToken).ConfigureAwait(false))
        {
            return ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen);
        }

        var failedAttempts = await FailedAttemptsAsync(db, input, now, cancellationToken).ConfigureAwait(false);
        if (failedAttempts >= options.Value.For(input.Kind).MaxFailedAttemptsPerRevision)
        {
            return ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted);
        }

        var attempt = failedAttempts + 1;
        var run = new ClaudeRun
        {
            Id = input.RunId,
            Kind = input.Kind,
            Subject = input.Subject,
            Scope = input.Scope,
            Revision = input.Revision,
            WorkflowId = workflowId,
            Attempt = attempt,
            ReservedAt = now,
            Model = input.Model,
        };
        if (_ledgers.TryGetValue(input.Kind, out var ledger))
        {
            ledger.MirrorLegacyColumns(run);
        }

        db.ClaudeRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ClaudeReservation.Reserved(input.RunId, attempt);
    }

    private async Task<int> FailedAttemptsAsync(RunDataDbContext db, ReserveClaudeRunInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_ledgers.TryGetValue(input.Kind, out var ledger))
        {
            return await ledger.FailedAttemptsAsync(db, input, cancellationToken).ConfigureAwait(false);
        }

        var dayStart = now - _day;
        var runs = db.ClaudeRuns
            .AsNoTracking()
            .Where(r => r.Kind == input.Kind && r.Subject == input.Subject && r.Revision == input.Revision && r.Outcome != null);
        if (input.Revision is null)
        {
            // Nothing resets this count when no revision moves on, so it ages out with the subject cap's rolling day.
            runs = runs.Where(r => (r.PodCreatedAt ?? r.ReservedAt) > dayStart);
        }

        var outcomes = await runs
            .Select(r => r.Outcome!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return outcomes.Count(o => o.IsFailure());
    }

    private async Task<ClaudeReservation?> CapBlockAsync(RunDataDbContext db, ReserveClaudeRunInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var caps = options.Value;
        var counted = await CountedRunsAsync(db, now, cancellationToken).ConfigureAwait(false);

        // Exempt runs skip the hourly and daily caps; they still count toward them for every other run.
        ClaudeReservation? latest = null;
        foreach (var (cause, until) in new[]
        {
            (ClaudeBudgetCauses.HourlyCap, input.ExemptFromGlobalCaps ? null : FreeAt(counted.Where(r => r.At > now - _hour), caps.MaxPerHour, _hour)),
            (ClaudeBudgetCauses.DailyCap, input.ExemptFromGlobalCaps ? null : FreeAt(counted, caps.MaxPerDay, _day)),
            (ClaudeBudgetCauses.PrDailyCap, FreeAt(counted.Where(r => r.Kind == input.Kind && r.Subject == input.Subject), caps.For(input.Kind).MaxPerSubjectPerDay, _day)),
        })
        {
            if (until is { } freeAt && (latest is null || freeAt > latest.Until))
            {
                latest = ClaudeReservation.Park(cause, freeAt);
            }
        }

        return latest;
    }

    private static Task<List<CountedRun>> CountedRunsAsync(RunDataDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dayStart = now - _day;
        var leaseStart = now - OpenReservationLease;
        return db.ClaudeRuns
            .AsNoTracking()
            .Where(r => (r.PodCreatedAt != null && r.PodCreatedAt > dayStart)
                || (r.PodCreatedAt == null && r.FinishedAt == null && r.ReservedAt > leaseStart))
            .Select(r => new CountedRun(r.Kind, r.Subject, r.PodCreatedAt ?? r.ReservedAt, r.PodCreatedAt == null))
            .ToListAsync(cancellationToken);
    }

    // A usage limit is global, not the scope's fault: the global cooldown covers it, so it neither opens nor closes the breaker.
    private static async Task<bool> BreakerOpenAsync(RunDataDbContext db, string kind, string scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recent = await db.ClaudeRuns
            .AsNoTracking()
            .Where(r => r.Kind == kind && r.Scope == scope && r.Outcome != null && r.Outcome != ClaudeRunOutcome.Cancelled && r.Outcome != ClaudeRunOutcome.UsageLimit && r.Outcome != ClaudeRunOutcome.NotStarted)
            .OrderByDescending(r => r.FinishedAt)
            .Take(BreakerThreshold)
            .Select(r => new { r.Outcome, r.FinishedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return recent.Count == BreakerThreshold
            && recent.All(r => r.Outcome!.Value.IsFailure())
            && recent[0].FinishedAt + _breakerOpenFor > now;
    }

    private sealed record CountedRun(string Kind, string Subject, DateTimeOffset At, bool Open);
}
