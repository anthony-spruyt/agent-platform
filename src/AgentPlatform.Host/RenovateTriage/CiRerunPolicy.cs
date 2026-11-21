using System.Net;
using System.Text.Json.Serialization;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<RerunOutcome>))]
public enum RerunOutcome
{
    Rerun,
    AttemptLimit,
    NothingFailed,
    Rebase,
    RebaseLimit,
    NoRebaseBox,
}

public sealed record CiRerunPlan(RerunOutcome Outcome, IReadOnlyList<long> RunIds);

public sealed record CiWait(DateTime? PendingSince, bool TimedOut)
{
    public static readonly CiWait Initial = new(null, false);

    public CiWait AfterRerun() => Initial;

    public CiWait Pending(DateTime now) => this with { PendingSince = PendingSince ?? now };

    public TimeSpan Remaining(DateTime now, TimeSpan limit)
    {
        var left = (PendingSince ?? now) + limit - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

public static class CiRerunPolicy
{
    public const int RerunBelowAttempt = 2;

    public static CiRerunPlan Decide(IReadOnlyCollection<GitHubJob> failedJobs)
    {
        ArgumentNullException.ThrowIfNull(failedJobs);
        if (failedJobs.Count == 0)
        {
            return new CiRerunPlan(RerunOutcome.NothingFailed, []);
        }

        return failedJobs.Any(j => j.RunAttempt >= RerunBelowAttempt)
            ? new CiRerunPlan(RerunOutcome.AttemptLimit, [])
            : new CiRerunPlan(RerunOutcome.Rerun, [.. failedJobs.Select(j => j.RunId).Distinct()]);
    }

    // GitHub answers 403 both for a run it will never re-run and for a token that lacks the permission; only the first is worth a rebase.
    public static bool IsRefusal(HttpStatusCode? status, string? message) =>
        status == HttpStatusCode.Forbidden
        && message is not null
        && (message.Contains("cannot be retried", StringComparison.OrdinalIgnoreCase) || message.Contains("cannot be re-run", StringComparison.OrdinalIgnoreCase));

    public static bool AwaitsNewAttempt(IReadOnlyCollection<GitHubJob> failedJobs)
    {
        ArgumentNullException.ThrowIfNull(failedJobs);
        return failedJobs.Count > 0 && failedJobs.All(j => j.RunAttempt < RerunBelowAttempt);
    }
}
