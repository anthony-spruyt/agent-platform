using AgentPlatform.Host.RunData;

namespace AgentPlatform.Host.ClaudeBudget;

// A kind that already keeps its own failed-attempt count registers one; the store then counts there instead of in claude_runs.
public interface IClaudeAttemptLedger
{
    string Kind { get; }

    Task<int> FailedAttemptsAsync(RunDataDbContext db, ReserveClaudeRunInput input, CancellationToken cancellationToken);

    // Runs in the transaction that records the failed outcome.
    Task RecordFailureAsync(RunDataDbContext db, ClaudeRun run, DateTimeOffset now, CancellationToken cancellationToken);
}
