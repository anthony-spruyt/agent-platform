using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class RenovateTriageActivitiesTests
{
    private const string Repo = "octo/widgets";
    private const string Sha = FakeGitHubApi.Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rig(RenovateTriageActivities Activities, FakeGitHubApi Github, FakeJev Jev, InMemoryRunData Data);

    private static Rig Create(TriageMode mode = TriageMode.Shadow)
    {
        var github = new FakeGitHubApi();
        var jev = new FakeJev();
        var data = InMemoryRunData.Create();
        var activities = new RenovateTriageActivities(github, jev, data.Verdicts, Options.Create(new RenovateTriageOptions { Mode = mode }));
        return new Rig(activities, github, jev, data);
    }

    private static Task<T> Run<T>(Func<Task<T>> action) => new ActivityEnvironment().RunAsync(action);

    private static Task<AssessResult> Assess(Rig rig) => Run(() => rig.Activities.AssessAsync(new AssessInput(Repo, 7)));

    public class AssessTests
    {
        [Fact]
        public async Task ARenovatePrWithPassingChecks_NeedsAJevJudgement()
        {
            var rig = Create();

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), result);
        }

        [Fact]
        public async Task TheHeadShaComesFromGitHub_NotFromTheSignal()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: "fedcba9876543210fedcba9876543210fedcba98");

            var result = await Assess(rig);

            Assert.Equal("fedcba9876543210fedcba9876543210fedcba98", result.HeadSha);
            Assert.Contains("checks:fedcba9876543210fedcba9876543210fedcba98", rig.Github.Calls);
        }

        [Fact]
        public async Task APrFromSomeoneElse_IsSkippedBeforeAnythingElseIsRead()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { AuthorLogin = "renovate[bot]", AuthorId = 12345 };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.NotRenovate), result);
            Assert.Equal(["pr"], rig.Github.Calls);
        }

        [Fact]
        public async Task ABotLoginWithoutTheRenovateId_IsStillSkipped()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = null };

            Assert.Equal(AssessStatus.Skipped, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ABlockingLabel_SkipsThePr()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/revert"] };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel), result);
        }

        [Theory]
        [InlineData("closed", false)]
        [InlineData("open", true)]
        public async Task AClosedOrMergedPr_IsSkipped(string state, bool merged)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = state, Merged = merged };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, RenovateTriageActivities.NotOpen), result);
        }

        [Fact]
        public async Task AnApprovedPr_IsSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "APPROVED", Sha, null));

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.AlreadyApproved), result);
        }

        [Fact]
        public async Task ApprovedThenChangesRequested_IsNotSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "APPROVED", Sha, null));
            rig.Github.Reviews.Add(new GitHubReview(2, "owner", "CHANGES_REQUESTED", Sha, null));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ACommitThatHasAVerdict_IsAlreadyJudged_WithoutReadingTheChecks()
        {
            var rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(), TriageMode.Shadow, Ct);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.AlreadyJudged, Sha), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PendingChecks_AreNotJudged()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha), await Assess(rig));
        }

        [Fact]
        public async Task TheStabilityDaysAndMergifyChecks_NeverHoldTheJudgement()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "renovate/stability-days", "in_progress", null, "renovate"));
            rig.Github.Checks.Add(new GitHubCheckRun(3, "Mergify Merge Protections", "queued", null, "mergify"));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AFailedCheck_DecidesNeedsReviewWithTheCiComment()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "cancelled", "github-actions"));

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.CiFailedDecision()), result);
        }

        [Fact]
        public async Task ARiskyBranch_DecidesNeedsReviewWithoutAskingJev()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate("renovate/traefik-3.x");

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.HighRiskDecision()), result);
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task AFailedCheck_WinsOverARiskyBranch()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate("renovate/traefik-3.x");
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

            Assert.Equal(TriageRules.CiFailed, (await Assess(rig)).Decision!.Reason);
        }

        [Fact]
        public async Task AFailedCheck_BeyondTheFirstPage_IsSeen()
        {
            var rig = Create();
            rig.Github.Checks.Clear();
            rig.Github.Checks.AddRange(Enumerable.Range(1, 120).Select(i => new GitHubCheckRun(i, $"job-{i}", "completed", i == 120 ? "failure" : "success", "github-actions")));

            Assert.Equal(AssessStatus.Decided, (await Assess(rig)).Status);
        }
    }

    public class FailureMapping
    {
        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Gone)]
        public async Task AClientErrorThatRetryingCannotFix_IsNotRetried(HttpStatusCode status)
        {
            var rig = Create();
            rig.Github.Failure = new GitHubApiException("read failed", status);

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Assess(rig));

            Assert.True(failure.NonRetryable);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.Unauthorized)]
        public async Task AServerOrRateLimitError_IsRetried(HttpStatusCode status)
        {
            var rig = Create();
            rig.Github.Failure = new GitHubApiException("read failed", status);

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => Assess(rig));

            Assert.IsNotType<ApplicationFailureException>(failure);
            Assert.IsType<GitHubApiException>(failure);
        }

        [Fact]
        public async Task ANetworkError_IsRetried()
        {
            var rig = Create();
            rig.Github.Failure = new HttpRequestException("reset");

            await Assert.ThrowsAsync<HttpRequestException>(() => Assess(rig));
        }

        [Fact]
        public async Task AnInvalidRepoName_IsNotRetried()
        {
            var rig = Create();

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.AssessAsync(new AssessInput("not-a-repo", 7))));

            Assert.True(failure.NonRetryable);
        }
    }

    public class JudgeTests
    {
        [Fact]
        public async Task JevIsAskedWithThePrBodyReadFromGitHub()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(body: "the notes");

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(["the notes"], rig.Jev.Bodies);
            Assert.False(result.HeadMoved);
            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), result.Decision);
            Assert.Equal(rig.Jev.Answers, result.Jev);
            Assert.Null(result.JevError);
        }

        [Fact]
        public async Task ABreakingBumpIsFlaggedWithTheBreakingComment()
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.9, "minor", 0.9);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.Breaking, TriageRules.BreakingComment), result.Decision);
        }

        [Fact]
        public async Task ANewerHeadCommit_IsReportedAndJevIsNotAsked()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: "fedcba9876543210fedcba9876543210fedcba98");

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.True(result.HeadMoved);
            Assert.Null(result.Decision);
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task AJevError_NeedsReviewWithoutAComment()
        {
            var rig = Create();
            rig.Jev.Failure = new JevException("Jev answered with status 502.");

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.JevError, null), result.Decision);
            Assert.Equal("Jev answered with status 502.", result.JevError);
            Assert.Null(result.Jev);
        }

        [Fact]
        public async Task ANullPrBody_IsPassedAsNull()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(body: null);

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Null(Assert.Single(rig.Jev.Bodies));
        }

        [Fact]
        public async Task AGitHubError_IsMappedLikeInAssess()
        {
            var rig = Create();
            rig.Github.Failure = new GitHubApiException("gone", HttpStatusCode.NotFound);

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha))));

            Assert.True(failure.NonRetryable);
        }
    }

    public class RecordTests
    {
        [Fact]
        public async Task TheVerdictIsStoredWithTheConfiguredMode()
        {
            var rig = Create();

            var result = await Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision())));

            Assert.Equal(new RecordVerdictResult(true, TriageMode.Shadow), result);
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            var row = await db.TriageVerdicts.SingleAsync(Ct);
            Assert.Equal(TriageMode.Shadow, row.Mode);
            Assert.Equal(TriageRules.HighRiskComment, row.Comment);
        }

        [Fact]
        public async Task ARepeatedVerdict_IsNotInsertedTwice()
        {
            var rig = Create();
            var input = new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision());
            await Run(() => rig.Activities.RecordVerdictAsync(input));

            var again = await Run(() => rig.Activities.RecordVerdictAsync(input));

            Assert.False(again.Inserted);
        }

        [Fact]
        public async Task ABadInput_FailsWithoutRetry()
        {
            var rig = Create();

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
                () => Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 0, Sha, TriageRules.HighRiskDecision()))));

            Assert.True(failure.NonRetryable);
        }
    }
}
