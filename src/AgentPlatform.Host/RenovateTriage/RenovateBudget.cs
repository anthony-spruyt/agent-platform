using System.Globalization;
using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RenovateTriage;

public static class RenovateBudget
{
    public const string Kind = "RenovateTriage";

    private const char SubjectSeparator = '#';

    public static string Subject(string repo, int prNumber) => string.Create(CultureInfo.InvariantCulture, $"{repo}{SubjectSeparator}{prNumber}");

    public static int PrNumber(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return int.Parse(subject.AsSpan(subject.LastIndexOf(SubjectSeparator) + 1), CultureInfo.InvariantCulture);
    }

    public static ReserveClaudeRunInput Reserve(Guid runId, string repo, int prNumber, string headSha, string model, bool security = false) =>
        new(runId, Kind, Subject(repo, prNumber), repo, headSha, model, security);

    public static RedriveCandidate Redrive(string repo, int prNumber, bool security = false) =>
        new(Kind, Subject(repo, prNumber), repo, security);
}

// The count lives on the pending row, which resets when the head moves.
public sealed class TriageAttemptLedger : IClaudeAttemptLedger
{
    public string Kind => RenovateBudget.Kind;

    public async Task<int> FailedAttemptsAsync(RunDataDbContext db, ReserveClaudeRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(input);

        var prNumber = RenovateBudget.PrNumber(input.Subject);
        return await db.TriagePending
            .AsNoTracking()
            .Where(p => p.Repo == input.Scope && p.PrNumber == prNumber && p.HeadSha == input.Revision)
            .Select(p => p.Attempts)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(RunDataDbContext db, ClaudeRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);

        var prNumber = RenovateBudget.PrNumber(run.Subject);
        await db.TriagePending
            .Where(p => p.Repo == run.Scope && p.PrNumber == prNumber && p.HeadSha == run.Revision)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Attempts, p => p.Attempts + 1).SetProperty(p => p.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }
}
