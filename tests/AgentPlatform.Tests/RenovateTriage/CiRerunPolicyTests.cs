using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class CiRerunPolicyTests
{
    private static GitHubJob Job(long id, long runId, int attempt) => new(id, runId, attempt, $"job-{id}", "failure");

    [Fact]
    public void FailedJobsOnTheFirstAttempt_AreRerun()
    {
        var plan = CiRerunPolicy.Decide([Job(1, 900, 1)]);

        Assert.Equal(RerunOutcome.Rerun, plan.Outcome);
        Assert.Equal([900L], plan.RunIds);
    }

    [Fact]
    public void FailedJobsOfTheSameRun_RerunThatRunOnce()
    {
        var plan = CiRerunPolicy.Decide([Job(1, 900, 1), Job(2, 900, 1), Job(3, 901, 1)]);

        Assert.Equal([900L, 901L], plan.RunIds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AJobOnItsSecondAttemptOrLater_IsNotRerun(int attempt)
    {
        var plan = CiRerunPolicy.Decide([Job(1, 900, attempt)]);

        Assert.Equal(RerunOutcome.AttemptLimit, plan.Outcome);
        Assert.Empty(plan.RunIds);
    }

    [Fact]
    public void OneRunPastTheLimit_StopsTheWholeRerun()
    {
        var plan = CiRerunPolicy.Decide([Job(1, 900, 1), Job(2, 901, 2)]);

        Assert.Equal(RerunOutcome.AttemptLimit, plan.Outcome);
        Assert.Empty(plan.RunIds);
    }

    [Fact]
    public void NoFailedJobs_HasNothingToRerun()
    {
        var plan = CiRerunPolicy.Decide([]);

        Assert.Equal(RerunOutcome.NothingFailed, plan.Outcome);
        Assert.Empty(plan.RunIds);
    }

    [Fact]
    public void FailedJobsAllOnTheFirstAttempt_AreStillTheOnesThatWereRerun()
    {
        Assert.True(CiRerunPolicy.AwaitsNewAttempt([Job(1, 900, 1), Job(2, 901, 1)]));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AFailedJobOnALaterAttempt_IsTheNewAttempt(int attempt)
    {
        Assert.False(CiRerunPolicy.AwaitsNewAttempt([Job(1, 900, 1), Job(2, 901, attempt)]));
    }

    [Fact]
    public void NoFailedJobs_AwaitNothing()
    {
        Assert.False(CiRerunPolicy.AwaitsNewAttempt([]));
    }

    [Fact]
    public void AfterARerun_TheCiWaitStartsFresh()
    {
        var spent = new CiWait(new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc), TimedOut: true);

        var fresh = spent.AfterRerun();

        Assert.Null(fresh.PendingSince);
        Assert.False(fresh.TimedOut);
        Assert.Equal(CiWait.Initial, fresh);
    }

    [Fact]
    public void TheFirstPendingAssessment_StartsTheClockOnce()
    {
        var start = new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);

        var first = CiWait.Initial.Pending(start);
        var later = first.Pending(start.AddMinutes(30));

        Assert.Equal(start, first.PendingSince);
        Assert.Equal(start, later.PendingSince);
    }

    [Fact]
    public void ARunningWait_ReportsTheTimeLeft_AndExpiresAtTheLimit()
    {
        var start = new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);
        var wait = CiWait.Initial.Pending(start);
        var limit = TimeSpan.FromHours(2);

        Assert.Equal(TimeSpan.FromMinutes(90), wait.Remaining(start.AddMinutes(30), limit));
        Assert.Equal(TimeSpan.Zero, wait.Remaining(start.AddHours(3), limit));
    }
}
