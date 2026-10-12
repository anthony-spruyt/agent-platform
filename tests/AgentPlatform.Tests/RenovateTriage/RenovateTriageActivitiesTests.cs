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

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.NotOpen), result);
        }

        [Fact]
        public async Task AnApprovedPr_IsSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", Sha, null));

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.AlreadyApproved), result);
        }

        [Fact]
        public async Task AnApprovalFromTheTriageApp_IsSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));

            Assert.Equal(TriageRules.AlreadyApproved, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task AnApprovalFromSomeoneWithoutWriteAccess_IsNotSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "stranger", "NONE", "APPROVED", Sha, null));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AnApprovalOfAnEarlierCommit_IsNotSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", "fedcba9876543210fedcba9876543210fedcba98", null));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ApprovedThenChangesRequested_IsNotSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", Sha, null));
            rig.Github.Reviews.Add(new GitHubReview(2, "owner", "OWNER", "CHANGES_REQUESTED", Sha, null));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Theory]
        [InlineData("fork/widgets", "octo/widgets")]
        [InlineData("octo/widgets", "fork/widgets")]
        [InlineData(null, "octo/widgets")]
        [InlineData("octo/widgets", null)]
        public async Task APrWhoseHeadAndBaseAreNotTheTriagedRepo_IsSkipped(string? headRepo, string? baseRepo)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { HeadRepo = headRepo, BaseRepo = baseRepo };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.ForeignRepo), result);
            Assert.Equal(["pr"], rig.Github.Calls);
        }

        [Fact]
        public async Task ThePrOfAnotherRepoThanTheInputRepo_IsSkipped()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { HeadRepo = "other/gadgets", BaseRepo = "other/gadgets" };

            Assert.Equal(TriageRules.ForeignRepo, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task RepoNamesAreComparedWithoutRegardToCase()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { HeadRepo = "Octo/Widgets", BaseRepo = "OCTO/widgets" };

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ACommitNotMadeByRenovate_DecidesNeedsReviewBeforeTheChecksAreRead()
        {
            var rig = Create();
            rig.Github.Commits.Add(rig.Github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 });
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.NonRenovateCommitDecision()), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
            Assert.Empty(rig.Jev.Bodies);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(0)]
        public async Task ACommitCountThatDiffersFromTheListedCommits_DecidesNeedsReview(int prCommits)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Commits = prCommits };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.NonRenovateCommitDecision()), result);
        }

        [Fact]
        public async Task APrWithTheMaximumListableCommits_DecidesNeedsReview()
        {
            var rig = Create();
            rig.Github.Commits.Clear();
            rig.Github.Commits.AddRange(Enumerable.Repeat(FakeGitHubApi.RenovateCommit(), TriageRules.MaxListedCommits));
            rig.Github.PullRequest = rig.Github.PullRequest with { Commits = TriageRules.MaxListedCommits };

            Assert.Equal(TriageRules.NonRenovateCommit, (await Assess(rig)).Decision!.Reason);
        }

        [Fact]
        public async Task AnUnverifiedCommit_DecidesNeedsReview()
        {
            var rig = Create();
            rig.Github.Commits[0] = rig.Github.Commits[0] with { Verified = false };

            Assert.Equal(TriageRules.NonRenovateCommit, (await Assess(rig)).Decision!.Reason);
        }

        [Fact]
        public async Task TheCommitsAreReadAfterTheAuthorAndApprovalAndJudgedChecks()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = 5 };
            await Assess(rig);
            Assert.DoesNotContain("commits", rig.Github.Calls);

            rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", Sha, null));
            await Assess(rig);
            Assert.DoesNotContain("commits", rig.Github.Calls);

            rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(), TriageMode.Shadow, Ct);
            await Assess(rig);
            Assert.DoesNotContain("commits", rig.Github.Calls);
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
        public async Task ASuiteThatHasNotCompleted_HoldsTheJudgementEvenWhenAllRunsAreDone()
        {
            var rig = Create();
            rig.Github.Suites.Add(new GitHubCheckSuite(2, "queued", null, "github-actions", 0));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha), await Assess(rig));
            Assert.Contains($"suites:{Sha}", rig.Github.Calls);
        }

        [Fact]
        public async Task NoCheckRunsAtAll_HoldTheJudgement()
        {
            var rig = Create();
            rig.Github.Checks.Clear();
            rig.Github.Suites.Clear();

            Assert.Equal(AssessStatus.Pending, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AMergifyCheckOrSuiteThatHasNotCompleted_NeverHoldsTheJudgement()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "Summary", "in_progress", null, "mergify"));
            rig.Github.Suites.Add(new GitHubCheckSuite(3, "in_progress", null, "mergify", 3));
            rig.Github.Suites.Add(new GitHubCheckSuite(4, "queued", null, "claude", 0));

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
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
            var body = "the notes";
            rig.Github.PullRequest = FakeGitHubApi.Renovate(body: body);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal([body], rig.Jev.Bodies);
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
        public async Task JevIsGivenThePrDiffWithTheLockFilesLast()
        {
            var rig = Create();
            rig.Github.Files.Clear();
            rig.Github.Files.Add(new GitHubPullRequestFile("package-lock.json", "@@\n-lock-old\n+lock-new"));
            rig.Github.Files.Add(new GitHubPullRequestFile("package.json", "@@\n-1.0.0\n+1.0.1"));
            rig.Github.Files.Add(new GitHubPullRequestFile("logo.png", null));

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(["### package.json\n@@\n-1.0.0\n+1.0.1\n\n### logo.png (no patch)\n\n### package-lock.json\n@@\n-lock-old\n+lock-new\n\n"], rig.Jev.Diffs);
        }

        [Fact]
        public async Task TheDiffIsCappedBeforeItIsSent()
        {
            var rig = Create();
            rig.Github.Files.Clear();
            rig.Github.Files.AddRange(Enumerable.Range(0, 2000).Select(i => new GitHubPullRequestFile($"f{i}.txt", new string('x', 1000))));

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            var sent = Assert.Single(rig.Jev.Diffs)!;
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(sent), JevClient.MaxPrDiffBytes, JevClient.MaxPrDiffBytes + 1100);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("release notes without any table")]
        public async Task AnyBody_IsJudgedByJevAlone(string? body)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Body = body };
            rig.Jev.Answers = new JevAnswers(0.04, "minor", 0.9);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal([body], rig.Jev.Bodies);
            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), result.Decision);
        }

        [Theory]
        [InlineData("major", TriageRules.MajorBump)]
        [InlineData("calver", TriageRules.CalverBump)]
        [InlineData("other", TriageRules.OtherBump)]
        [InlineData("digest", TriageRules.Digest)]
        public async Task TheUpdateTypeIsWhatJevSays(string bump, string reason)
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.04, bump, 0);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(reason, result.Decision!.Reason);
        }

        [Fact]
        public async Task TheFilesAreNotReadWhenTheHeadMoved()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: "fedcba9876543210fedcba9876543210fedcba98");

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.DoesNotContain("files", rig.Github.Calls);
        }

        [Fact]
        public async Task AnUnexpectedFailureAfterJevWasAsked_IsNotRetried()
        {
            var rig = Create();
            rig.Jev.Failure = new InvalidOperationException("boom");

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha))));

            Assert.True(failure.NonRetryable);
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
