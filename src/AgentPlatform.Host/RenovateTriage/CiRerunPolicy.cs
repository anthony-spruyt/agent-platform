using System.Text.Json.Serialization;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<RerunOutcome>))]
public enum RerunOutcome
{
    Rerun,
    AttemptLimit,
    NothingFailed,
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

    public static bool AwaitsNewAttempt(IReadOnlyCollection<GitHubJob> failedJobs)
    {
        ArgumentNullException.ThrowIfNull(failedJobs);
        return failedJobs.Count > 0 && failedJobs.All(j => j.RunAttempt < RerunBelowAttempt);
    }
}
