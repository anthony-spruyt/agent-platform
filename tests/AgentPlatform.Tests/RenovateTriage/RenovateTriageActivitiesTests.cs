using System.Net;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class RenovateTriageActivitiesTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = FakeGitHubApi.Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rig(RenovateTriageActivities Activities, FakeGitHubApi Github, FakeJev Jev, InMemoryRunData Data, FakeTextScanner Scanner);

    private static Rig Create(TriageMode mode = TriageMode.Shadow)
    {
        var github = new FakeGitHubApi();
        var jev = new FakeJev();
        var data = InMemoryRunData.Create();
        var scanner = new FakeTextScanner();
        var activities = new RenovateTriageActivities(github, jev, data.Verdicts, data.Pending, Options.Create(new RenovateTriageOptions { Mode = mode }), scanner);
        return new Rig(activities, github, jev, data, scanner);
    }

    private static Task<T> Run<T>(Func<Task<T>> action) => new ActivityEnvironment().RunAsync(action);

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

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
        [InlineData("blocked")]
        [InlineData("agent/revert")]
        public async Task InShadowMode_ABlockedOrRevertLabel_SkipsThePr(string label)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review", label] };

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel), await Assess(rig));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/auto-approved")]
        public async Task InShadowMode_ALabelFromTheLivePath_DoesNotSkipThePr(string label)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), await Assess(rig));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/revert")]
        [InlineData("blocked")]
        public async Task InLiveMode_EveryBlockingLabel_SkipsThePr(string label)
        {
            var rig = Create(TriageMode.Live);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), await Assess(rig));
        }

        [Theory]
        [InlineData("closed", false)]
        [InlineData("closed", true)]
        [InlineData("open", true)]
        public async Task InLiveMode_AClosedOrMergedPr_IsSkipped(string state, bool merged)
        {
            var rig = Create(TriageMode.Live);
            rig.Github.PullRequest = rig.Github.PullRequest with { State = state, Merged = merged };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.NotOpen, Mode: TriageMode.Live), result);
        }

        [Fact]
        public async Task InShadowMode_AClosedUnmergedPr_IsSkipped()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = "closed", Merged = false };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.NotOpen), result);
        }

        [Fact]
        public async Task InShadowMode_AMergedPr_ContinuesThroughTheRules()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = "closed", Merged = true };

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), await Assess(rig));
        }

        [Fact]
        public async Task InShadowMode_AMergedPrWithRunningChecks_IsPending()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = "closed", Merged = true };
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha), await Assess(rig));
        }

        [Fact]
        public async Task InShadowMode_AMergedPrFromAnotherRepo_IsStillSkipped()
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = "closed", Merged = true, HeadRepo = "fork/widgets" };

            Assert.Equal(TriageRules.ForeignRepo, (await Assess(rig)).Reason);
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
        public async Task InLiveMode_AnApprovalFromTheTriageApp_IsSkipped()
        {
            var rig = Create(TriageMode.Live);
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));

            Assert.Equal(TriageRules.AlreadyApproved, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task InShadowMode_AnApprovalFromTheTriageApp_IsNotSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), await Assess(rig));
        }

        [Fact]
        public async Task InShadowMode_AHumanApprovalBesideTheTriageApp_IsSkipped()
        {
            var rig = Create();
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));
            rig.Github.Reviews.Add(new GitHubReview(2, "owner", "OWNER", "APPROVED", Sha, null));

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.AlreadyApproved), await Assess(rig));
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
        [InlineData("fork/widgets", "anthony-spruyt/widgets")]
        [InlineData("anthony-spruyt/widgets", "fork/widgets")]
        [InlineData(null, "anthony-spruyt/widgets")]
        [InlineData("anthony-spruyt/widgets", null)]
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
            rig.Github.PullRequest = rig.Github.PullRequest with { HeadRepo = "Anthony-Spruyt/Widgets", BaseRepo = "ANTHONY-SPRUYT/widgets" };

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

        [Fact]
        public async Task ACommitThatOnlyMergesTheBaseIn_StillNeedsAJevJudgement_AtTheMergedHead()
        {
            var rig = Create();
            rig.Github.MergeBaseIn();

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, FakeGitHubApi.MergeSha), result);
        }

        [Fact]
        public async Task AMergeThatAddsMoreThanTheBase_DecidesNeedsReview()
        {
            var rig = Create();
            rig.Github.MergeBaseIn();
            rig.Github.Comparisons[$"{FakeGitHubApi.Sha}...{FakeGitHubApi.MergeSha}"] = new GitHubComparison("ahead", null, [new GitHubCompareFile("deploy.sh", "added", "ccc")]);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, FakeGitHubApi.MergeSha, Decision: TriageRules.NonRenovateCommitDecision()), result);
        }

        [Fact]
        public async Task ATransientErrorReadingTheComparisons_FailsTheActivityInsteadOfRecordingAVerdict()
        {
            var rig = Create();
            rig.Github.MergeBaseIn();
            rig.Github.CompareFailure = new GitHubApiException("GitHub comparison read failed with status 503.", HttpStatusCode.ServiceUnavailable);

            var failure = await Assert.ThrowsAsync<GitHubApiException>(() => Assess(rig));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
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
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, null, Ct);
            await Assess(rig);
            Assert.DoesNotContain("commits", rig.Github.Calls);
        }

        [Fact]
        public async Task ACommitThatHasAVerdict_IsAlreadyJudged_WithoutReadingTheChecks()
        {
            var rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, null, Ct);

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
        public async Task AFailedCheck_DecidesNeedsReviewWithTheCiCommentNamingIt()
        {
            var rig = Create();
            var failed = new GitHubCheckRun(2, "test", "completed", "cancelled", "github-actions");
            rig.Github.Checks.Add(failed);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.CiFailedDecision(Repo, Sha, [failed])), result);
            Assert.Contains("[`test`](https://github.com/anthony-spruyt/widgets/runs/2)", result.Decision!.Comment, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AFailedCommitStatus_DecidesNeedsReviewNamingItWithoutALink()
        {
            var rig = Create();
            var failed = new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "completed", "failure", null);
            rig.Github.Checks.Add(failed);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.CiFailedDecision(Repo, Sha, [failed])), result);
            Assert.EndsWith(" Failed: `Terraform Cloud/spruyt-labs/repo`.", result.Decision!.Comment, StringComparison.Ordinal);
        }

        [Fact]
        public async Task APendingCommitStatus_HoldsTheJudgement()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "pending", null, null));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha, StatusPending: true), await Assess(rig));
        }

        [Fact]
        public async Task ACommitStatusAmongPendingActionsRuns_StillAsksForAStatusPoll()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "pending", null, null));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha, StatusPending: true), await Assess(rig));
        }

        [Fact]
        public async Task PendingActionsRunsNextToACompletedCommitStatus_DoNotAskForAStatusPoll()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "completed", "success", null));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha), await Assess(rig));
        }

        [Fact]
        public async Task AnIgnoredPendingCommitStatus_NeverAsksForAStatusPoll()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "renovate/stability-days", "pending", null, null));

            Assert.Equal(new AssessResult(AssessStatus.Pending, Sha), await Assess(rig));
        }

        [Fact]
        public async Task ARiskyBranch_DecidesNeedsReviewWithoutAskingJev()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate("renovate/traefik-3.x");

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: TriageRules.HighRiskDecision("traefik")), result);
            Assert.Contains("`traefik` is on the high-risk list", result.Decision!.Comment, StringComparison.Ordinal);
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

    public class Step2AssessTests
    {
        private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";

        private static Task<AssessResult> AssessStep2(Rig rig, string? prechecked = null) =>
            Run(() => rig.Activities.AssessAsync(new AssessInput(Repo, 7, Step2: true, PrecheckedSha: prechecked)));

        [Fact]
        public async Task ARenovatePrThatPassedTheCommitGate_NeedsThePrecheckBeforeTheChecksAreRead()
        {
            var rig = Create();

            var result = await AssessStep2(rig);

            Assert.Equal(new AssessResult(AssessStatus.NeedsPrecheck, Sha), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
        }

        [Fact]
        public async Task OnceTheHeadIsPrechecked_TheRulesRunAsBefore()
        {
            var rig = Create();

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), await AssessStep2(rig, Sha));
        }

        [Fact]
        public async Task APrecheckOfAnOlderHead_DoesNotCount()
        {
            var rig = Create();

            Assert.Equal(AssessStatus.NeedsPrecheck, (await AssessStep2(rig, OtherSha)).Status);
        }

        [Fact]
        public async Task ACommitNotMadeByRenovate_IsDecidedBeforeThePrecheck()
        {
            var rig = Create();
            rig.Github.Commits.Add(rig.Github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 });

            Assert.Equal(AssessStatus.Decided, (await AssessStep2(rig)).Status);
        }

        [Fact]
        public async Task ARedHead_IsHandedToTheCiRungWithTheCiFailedDecision()
        {
            var rig = Create();
            var failed = new GitHubCheckRun(2, "test", "completed", "failure", "github-actions");
            rig.Github.Checks.Add(failed);

            var result = await AssessStep2(rig, Sha);

            Assert.Equal(new AssessResult(AssessStatus.CiFailed, Sha, Decision: TriageRules.CiFailedDecision(Repo, Sha, [failed])), result);
        }

        [Fact]
        public async Task APendingHead_StaysPending()
        {
            var rig = Create();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

            Assert.Equal(AssessStatus.Pending, (await AssessStep2(rig, Sha)).Status);
        }
    }

    public class ClaudeAssessTests
    {
        private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";

        private static Rig Live() => Create(TriageMode.Live);

        private static Task<AssessResult> AssessClaude(Rig rig, string? prechecked = Sha) =>
            Run(() => rig.Activities.AssessAsync(new AssessInput(Repo, 7, Step2: true, PrecheckedSha: prechecked, Claude: true)));

        private static void Label(Rig rig, params string[] labels) =>
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = labels };

        [Fact]
        public async Task APendingHead_GoesStraightToTheClaudeRung_WithTheStoredReason()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);

            var result = await AssessClaude(rig);

            Assert.Equal(new AssessResult(AssessStatus.Climb, Sha, TriageRules.CiFailed, Mode: TriageMode.Live), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task APendingHead_StillGetsTheCommitGate()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);
            rig.Github.Commits.Add(rig.Github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 });

            var result = await AssessClaude(rig);

            Assert.Equal((AssessStatus.Decided, TriageRules.NonRenovateCommit), (result.Status, result.Decision!.Reason));
        }

        [Fact]
        public async Task APendingHead_StillGetsThePrecheck()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);

            Assert.Equal(AssessStatus.NeedsPrecheck, (await AssessClaude(rig, prechecked: null)).Status);
        }

        [Fact]
        public async Task APendingHead_WithAnOwnerLabel_IsStillSkipped()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel, "blocked");
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), await AssessClaude(rig));
        }

        [Fact]
        public async Task ARowForAnEarlierHead_LetsTheTriageLabelThrough_AndTheNewHeadIsJudgedFresh()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, OtherSha, TriageRules.CiFailed, Ct);

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha, Mode: TriageMode.Live), await AssessClaude(rig));
        }

        [Fact]
        public async Task TheTriageLabelWithoutARow_BlocksSoAHandAddedLabelNeverStartsARun()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), await AssessClaude(rig));
        }

        [Theory]
        [InlineData(TriagePendingStatus.Settled)]
        [InlineData(TriagePendingStatus.Owner)]
        [InlineData(TriagePendingStatus.OwnerHandled)]
        public async Task AClosedRow_DoesNotLetTheTriageLabelThrough(TriagePendingStatus status)
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);
            await rig.Data.Pending.SetStatusAsync(Repo, 7, Sha, status, Ct);

            Assert.Equal(AssessStatus.Skipped, (await AssessClaude(rig)).Status);
        }

        [Fact]
        public async Task WithoutTheClaudeFlag_ARowIsIgnored()
        {
            var rig = Live();
            Label(rig, TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);

            var result = await Run(() => rig.Activities.AssessAsync(new AssessInput(Repo, 7, Step2: true, PrecheckedSha: Sha)));

            Assert.Equal(AssessStatus.Skipped, result.Status);
        }

        [Fact]
        public async Task InShadowMode_ARowIsIgnored()
        {
            var rig = Create();
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await AssessClaude(rig)).Status);
        }

        [Fact]
        public async Task AStoredVerdict_IsMarkedStored_SoItIsNeverClimbed()
        {
            var rig = Live();
            var decision = new TriageDecision(TriageVerdict.NeedsReview, TriageRules.CiFailed, "**Needs review:** CI failed.");
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, null, Ct);

            var result = await AssessClaude(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: decision, Mode: TriageMode.Live, Stored: true), result);
        }

        [Fact]
        public async Task AFreshDecision_IsNotStored()
        {
            var rig = Live();
            rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

            var result = await AssessClaude(rig);

            Assert.False(result.Stored);
        }
    }

    public class LiveAssessTests
    {
        private static Rig Live() => Create(TriageMode.Live);

        [Fact]
        public async Task EveryResultNamesTheModeItWasMadeIn()
        {
            var rig = Live();

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha, Mode: TriageMode.Live), await Assess(rig));
            Assert.Equal(TriageMode.Shadow, (await Assess(Create())).Mode);
        }

        [Fact]
        public async Task ARepoOfAnotherOwner_IsSkippedBeforeAnythingIsRead()
        {
            var rig = Live();

            var result = await Run(() => rig.Activities.AssessAsync(new AssessInput("octo/widgets", 7)));

            Assert.Equal(AssessStatus.Skipped, result.Status);
            Assert.Equal(TriageRules.ForeignOwner, result.Reason);
            Assert.Equal(TriageMode.Live, result.Mode);
            Assert.Empty(rig.Github.Calls);
        }

        [Fact]
        public async Task ARepoOfAnotherOwner_IsSkippedInShadowToo()
        {
            var rig = Create();

            var result = await Run(() => rig.Activities.AssessAsync(new AssessInput("octo/widgets", 7)));

            Assert.Equal(TriageRules.ForeignOwner, result.Reason);
            Assert.Empty(rig.Github.Calls);
        }

        [Fact]
        public async Task AnAppliedLiveVerdict_IsAlreadyJudged()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);
            await rig.Data.Verdicts.MarkAppliedAsync(Repo, 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

            Assert.Equal(new AssessResult(AssessStatus.AlreadyJudged, Sha, Mode: TriageMode.Live), await Assess(rig));
        }

        [Fact]
        public async Task AShadowVerdict_DoesNotCountAsJudgedInLive()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, null, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ALiveVerdictDoesNotCountAsJudgedInShadow()
        {
            var rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsDecidedAgainFromTheStoredRow()
        {
            var rig = Live();
            var decision = TriageRules.CiFailedDecision(Repo, Sha, [new GitHubCheckRun(3, "build", "completed", "failure", "github-actions")]);
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, null, Ct);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: decision, Mode: TriageMode.Live, Stored: true), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsReappliedDespiteTheLabelAndApprovalOfItsOwnWrites()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), TriageMode.Live, null, Ct);
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.AutoApprovedLabel] };

            Assert.Equal(AssessStatus.Decided, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AnUnappliedNeedsReviewVerdict_IsReappliedDespiteTheNeedsReviewLabelOfItsOwnWrites()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review"] };

            Assert.Equal(AssessStatus.Decided, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AnUnappliedApproveVerdict_IsStillHeldBackByAHumansNeedsReviewLabel()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), TriageMode.Live, null, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review"] };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), result);
        }

        [Fact]
        public async Task AnUnappliedSafeVerdict_IsStillHeldBackByAHumansNeedsReviewLabel()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "claude-safe", "**Agent verdict: SAFE**"), TriageMode.Live, null, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review"] };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), result);
        }

        [Fact]
        public async Task AnUnappliedSafeVerdict_IsReappliedDespiteTheParkLabel()
        {
            var rig = Live();
            var decision = new TriageDecision(TriageVerdict.Safe, "claude-safe", "**Agent verdict: SAFE**");
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, null, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-triage"] };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: decision, Mode: TriageMode.Live, Stored: true), result);
        }

        [Theory]
        [InlineData("blocked")]
        [InlineData("agent/revert")]
        public async Task AnUnappliedLiveVerdict_IsStillHeldBackByAHumansBlockingLabel(string label)
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(TriageRules.BlockingLabel, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsStillHeldBackByAHumanApproval()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", Sha, null));

            Assert.Equal(TriageRules.AlreadyApproved, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task AnUnappliedVerdictOfAnEarlierCommit_DoesNotShortcutTheNewOne()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, new string('a', 40), TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, null, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }
    }

    public class JudgeGuardTests
    {
        private static Task<JudgeResult> Judge(Rig rig, bool retryJevErrors = false) =>
            Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha, retryJevErrors)));

        [Fact]
        public async Task AVersionPairThatDisagreesWithJev_NeedsReview()
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.9);
            rig.Github.Files.Clear();
            rig.Github.Files.Add(new GitHubPullRequestFile("package.json", "@@ -1 +1 @@\n-\"dep\": \"1.0.0\"\n+\"dep\": \"1.2.0\""));

            var result = await Judge(rig);

            Assert.Equal(TriageRules.BumpTypeMismatch, result.Decision!.Reason);
            Assert.Equal(rig.Jev.Answers, result.Jev);
        }

        [Theory]
        [InlineData("-a: 1.0.0\n+a: 1.0.1 and 2.0.0")]
        [InlineData("-a: 1.0.0\n+a: latest")]
        [InlineData("-a: latest\n+a: 1.0.1")]
        public async Task AChangedLinePairWhoseVersionsCannotBePaired_NeedsReview(string lines)
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.9);
            rig.Github.Files.Clear();
            rig.Github.Files.Add(new GitHubPullRequestFile("package.json", $"@@ -1 +1 @@\n{lines}"));

            var result = await Judge(rig);

            Assert.Equal(TriageRules.BumpTypeMismatch, result.Decision!.Reason);
        }

        [Fact]
        public async Task ADigestBumpThatChangesMoreThanADigest_NeedsReview()
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.04, "digest", 0);

            var result = await Judge(rig);

            Assert.Equal(TriageRules.DigestMismatch, result.Decision!.Reason);
        }

        [Fact]
        public async Task TheMajorCommentNamesTheParsedVersions()
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.04, "major", 0);
            rig.Github.Files.Clear();
            rig.Github.Files.Add(new GitHubPullRequestFile("package.json", "@@ -1 +1 @@\n-\"dep\": \"1.9.0\"\n+\"dep\": \"2.0.0\""));

            var result = await Judge(rig);

            Assert.Equal("**Needs review:** major bump, `1.9.0` -> `2.0.0`.", result.Decision!.Comment);
        }

        [Fact]
        public async Task AJevError_WhenRetriesAreRequested_FailsRetryablyWithATypeTheWorkflowRecognises()
        {
            var rig = Create();
            rig.Jev.Failure = new JevException("Jev answered with status 502.");

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Judge(rig, retryJevErrors: true));

            Assert.False(failure.NonRetryable);
            Assert.Equal(RenovateTriageActivities.JevErrorFailureType, failure.ErrorType);
        }

        [Fact]
        public async Task AJevError_WhenRetriesAreNotRequested_KeepsReturningTheErrorVerdict()
        {
            var rig = Create();
            rig.Jev.Failure = new JevException("Jev answered with status 502.");

            var result = await Judge(rig);

            Assert.Equal(TriageRules.JevError, result.Decision!.Reason);
            Assert.NotNull(result.JevError);
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
        public async Task JevGetsExactlyTheScannedBody_EvenWhenThePrBodyChangedAfterTheScan()
        {
            var rig = Create();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(body: "edited after the scan");

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha, Body: ScannedText.Create("what was scanned"))));

            Assert.Equal(["what was scanned"], rig.Jev.Bodies);
        }

        [Fact]
        public async Task ScannedChangelogSections_FollowTheBodyInWhatJevReads()
        {
            var rig = Create();

            await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha, Body: ScannedText.Create("body"), Changelogs: [ScannedText.Create("- fixed one"), ScannedText.Create("- fixed two")])));

            var read = Assert.Single(rig.Jev.Bodies)!;
            Assert.StartsWith("body", read, StringComparison.Ordinal);
            Assert.True(read.IndexOf("- fixed one", StringComparison.Ordinal) < read.IndexOf("- fixed two", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ABodyThatNoLongerMatchesItsHash_IsRefusedWithoutAskingJev()
        {
            var rig = Create();
            var tampered = new ScannedText(ScannedText.Create("what was scanned").Hash, "what was swapped in");

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha, Body: tampered))));

            Assert.True(failure.NonRetryable);
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task AChangelogThatNoLongerMatchesItsHash_IsRefusedWithoutAskingJev()
        {
            var rig = Create();
            var tampered = new ScannedText(ScannedText.Create("scanned").Hash, "swapped");

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha, Body: ScannedText.Create("body"), Changelogs: [tampered]))));

            Assert.True(failure.NonRetryable);
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task JevIsAskedWithThePrBodyReadFromGitHub()
        {
            var rig = Create();
            var body = "the notes";
            rig.Github.PullRequest = FakeGitHubApi.Renovate(body: body);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal([body], rig.Jev.Bodies);
            Assert.False(result.HeadMoved);
            Assert.Equal(new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, "**Auto-approved:** patch bump with changelog."), result.Decision);
            Assert.Equal(rig.Jev.Answers, result.Jev);
            Assert.Null(result.JevError);
        }

        [Fact]
        public async Task ABreakingBumpIsFlaggedWithTheBreakingComment()
        {
            var rig = Create();
            rig.Jev.Answers = new JevAnswers(0.9, "minor", 0.9);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.Breaking, "**Needs review:** likely breaking changes (Jev score 0.90)."), result.Decision);
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
        public async Task AJevError_NeedsReviewWithTheJevErrorComment()
        {
            var rig = Create();
            rig.Jev.Failure = new JevException("Jev answered with status 502.");

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal(new TriageDecision(TriageVerdict.NeedsReview, TriageRules.JevError, "**Needs review:** the triage check couldn't run (Jev error)."), result.Decision);
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
            rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.9);

            var result = await Run(() => rig.Activities.JudgeAsync(new JudgeInput(Repo, 7, Sha)));

            Assert.Equal([body], rig.Jev.Bodies);
            Assert.Equal(TriageRules.ChangelogPresent, result.Decision!.Reason);
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
            rig.Github.Files.Clear();
            rig.Github.Files.Add(new GitHubPullRequestFile("image.yaml", $"@@ -1 +1 @@\n-image: app@sha256:{new string('a', 64)}\n+image: app@sha256:{new string('b', 64)}"));

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

    public class ApplyTests
    {
        private static readonly TriageDecision _approve = new(TriageVerdict.Approve, TriageRules.ChangelogPresent, "**Auto-approved:** patch bump with changelog.");
        private static readonly TriageDecision _needsReview = new(TriageVerdict.NeedsReview, TriageRules.NoChangelog, "**Needs review:** no changelog found for this bump.");

        private static Rig Live() => Create(TriageMode.Live);

        private static Task<ApplyResult> Apply(Rig rig, TriageDecision decision, string sha = Sha, TriageMode mode = TriageMode.Live) =>
            Run(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 7, sha, decision, mode)));

        private static async Task Record(Rig rig, TriageDecision decision) =>
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, null, Ct);

        private static GitHubReview BotApproval(long id, string commit) => new(id, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", commit, null);

        private static string Sticky(string text) => $"{TriageRules.StickyMarker}\n{text}";

        [Fact]
        public async Task Approve_ReviewsThePinnedCommitAndLabelsThePr()
        {
            var rig = Live();
            await Record(rig, _approve);

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal((Sha, "**Auto-approved:** patch bump with changelog."), Assert.Single(rig.Github.Approvals));
            Assert.Equal([TriageRules.AutoApprovedLabel], rig.Github.AddedLabels);
            Assert.Empty(rig.Github.Comments);
        }

        [Fact]
        public async Task Approve_MarksTheVerdictApplied()
        {
            var rig = Live();
            await Record(rig, _approve);

            await Apply(rig, _approve);

            Assert.True(await rig.Data.Verdicts.ExistsAsync(Repo, 7, Sha, TriageMode.Live, Ct));
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task Approve_StoresTheApproveAction()
        {
            var rig = Live();
            await Record(rig, _approve);

            await Apply(rig, _approve);

            Assert.Equal(VerdictAction.Approve, await ActionAsync(rig));
        }

        [Fact]
        public async Task Approve_ThatOnlyLabelsBecauseTheAppAlreadyApproved_StillStoresTheApproveAction()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, Sha));
            await Record(rig, _approve);

            await Apply(rig, _approve);

            Assert.Equal(VerdictAction.Approve, await ActionAsync(rig));
        }

        [Fact]
        public async Task NeedsReview_StoresTheCommentAction()
        {
            var rig = Live();
            await Record(rig, _needsReview);

            await Apply(rig, _needsReview);

            Assert.Equal(VerdictAction.Comment, await ActionAsync(rig));
        }

        private static async Task<VerdictAction?> ActionAsync(Rig rig)
        {
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            return (await db.TriageVerdicts.SingleAsync(Ct)).Action;
        }

        [Fact]
        public async Task Approve_SkipsTheReviewWhenTheAppAlreadyApprovedThisCommit_ButStillLabels()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, Sha));

            await Apply(rig, _approve);

            Assert.Empty(rig.Github.Approvals);
            Assert.Equal([TriageRules.AutoApprovedLabel], rig.Github.AddedLabels);
        }

        [Fact]
        public async Task Approve_ReviewsAgainWhenTheEarlierApprovalWasForAnotherCommit()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));

            await Apply(rig, _approve);

            Assert.Single(rig.Github.Approvals);
        }

        [Fact]
        public async Task Approve_RunTwice_WritesTheReviewOnce()
        {
            var rig = Live();

            await Apply(rig, _approve);
            await Apply(rig, _approve);

            Assert.Single(rig.Github.Approvals);
        }

        [Fact]
        public async Task Approve_UpdatesAnEarlierNeedsReviewStickyCommentInsteadOfLeavingItStale()
        {
            var rig = Live();
            rig.Github.Comments.Add(new GitHubIssueComment(40, TriageRules.TriageBotLogin, Sticky("**Needs review:** no changelog found for this bump.")));

            await Apply(rig, _approve);

            var comment = Assert.Single(rig.Github.Comments);
            Assert.Equal(Sticky($"**Auto-approved:** new commit `{TriageRules.ShortSha(Sha)}`, patch bump with changelog."), comment.Body);
            Assert.Contains("comment-update:40", rig.Github.Writes);
        }

        [Theory]
        [InlineData("**Auto-approved:** digest bump.", "digest bump")]
        [InlineData("**Auto-approved:** minor bump with changelog.", "minor bump with changelog")]
        [InlineData("**Auto-approved:** allowlisted.", "allowlisted")]
        public async Task Approve_TheStickyEditGivesTheSameReasonAsTheReviewBody(string reviewBody, string reason)
        {
            var rig = Live();
            rig.Github.Comments.Add(new GitHubIssueComment(40, TriageRules.TriageBotLogin, Sticky("**Needs review:** no changelog found for this bump.")));

            await Apply(rig, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, reviewBody));

            Assert.Equal(reviewBody, Assert.Single(rig.Github.Approvals).Body);
            Assert.Equal(Sticky($"**Auto-approved:** new commit `{TriageRules.ShortSha(Sha)}`, {reason}."), Assert.Single(rig.Github.Comments).Body);
        }

        [Fact]
        public async Task Approve_IgnoresAMarkerCommentFromSomeoneElse()
        {
            var rig = Live();
            rig.Github.Comments.Add(new GitHubIssueComment(40, "mallory", Sticky("planted")));

            await Apply(rig, _approve);

            Assert.Equal(Sticky("planted"), Assert.Single(rig.Github.Comments).Body);
            Assert.DoesNotContain(rig.Github.Writes, w => w.StartsWith("comment", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NeedsReview_ClearsAnOlderHeadsVerdictLabel_AndKeepsOtherLabels()
        {
            var rig = Live();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.SafeLabel, "dependencies"] };
            await Record(rig, _needsReview);

            await Apply(rig, _needsReview);

            Assert.Equal([TriageRules.SafeLabel], rig.Github.RemovedLabels);
            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
            Assert.Contains("dependencies", rig.Github.PullRequest.Labels);
        }

        [Fact]
        public async Task Approve_ClearsAnOlderHeadsVerdictLabels()
        {
            var rig = Live();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.SafeLabel, TriageRules.FixableLabel] };
            await Record(rig, _approve);

            await Apply(rig, _approve);

            Assert.Equal([TriageRules.SafeLabel, TriageRules.FixableLabel], rig.Github.RemovedLabels);
            Assert.Equal([TriageRules.AutoApprovedLabel], rig.Github.AddedLabels);
        }

        [Fact]
        public async Task NeedsReview_LabelsAndPostsTheStickyComment()
        {
            var rig = Live();
            await Record(rig, _needsReview);

            var result = await Apply(rig, _needsReview);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
            Assert.Equal(Sticky(_needsReview.Comment!), Assert.Single(rig.Github.Comments).Body);
            Assert.Empty(rig.Github.Approvals);
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task NeedsReview_EditsTheExistingStickyCommentForANewCommit()
        {
            var rig = Live();
            rig.Github.Comments.Add(new GitHubIssueComment(40, TriageRules.TriageBotLogin, Sticky("**Needs review:** CI failed on `aaaaaaa`.")));

            await Apply(rig, _needsReview);

            Assert.Equal(Sticky(_needsReview.Comment!), Assert.Single(rig.Github.Comments).Body);
            Assert.Contains("comment-update:40", rig.Github.Writes);
            Assert.DoesNotContain(rig.Github.Writes, w => w.StartsWith("comment-create", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NeedsReview_RunTwice_NeverDuplicatesTheComment()
        {
            var rig = Live();

            await Apply(rig, _needsReview);
            await Apply(rig, _needsReview);

            Assert.Single(rig.Github.Comments);
            Assert.Single(rig.Github.Writes, w => w.StartsWith("comment", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NeedsReview_DismissesEveryApprovalOfTheApp_EvenOneOnTheSameCommit()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));
            rig.Github.Reviews.Add(BotApproval(2, Sha));
            rig.Github.Reviews.Add(new GitHubReview(3, "owner", "OWNER", "APPROVED", new string('a', 40), null));
            rig.Github.Reviews.Add(new GitHubReview(4, TriageRules.TriageBotLogin, "CONTRIBUTOR", "DISMISSED", new string('c', 40), null));

            await Apply(rig, _needsReview);

            Assert.Equal([1L, 2L], rig.Github.Dismissed);
        }

        [Fact]
        public async Task NeedsReview_TheDismissalMessageIsTheApprovedText()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));

            await Apply(rig, _needsReview);

            Assert.Equal("A newer commit needs review.", Assert.Single(rig.Github.DismissalMessages));
        }

        [Fact]
        public async Task NeedsReview_DismissesBeforeItLabelsAndComments()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));

            await Apply(rig, _needsReview);

            Assert.Equal(["dismiss:1", $"labels:{TriageRules.NeedsReviewLabel}", "comment-create:500"], rig.Github.Writes);
        }

        [Fact]
        public async Task NeedsReview_WithoutAComment_OnlyLabels()
        {
            var rig = Live();

            await Apply(rig, new TriageDecision(TriageVerdict.NeedsReview, TriageRules.NoChangelog, null));

            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
            Assert.Empty(rig.Github.Comments);
        }

        [Fact]
        public async Task NeedsReview_ADismissalGitHubRefuses_DoesNotStopTheRest()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));
            rig.Github.OnDismiss = () => throw new GitHubApiException("already dismissed", HttpStatusCode.UnprocessableEntity);

            var result = await Apply(rig, _needsReview);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        }

        [Fact]
        public async Task NeedsReview_ARefusedDismissal_IsLoggedAsAWarning()
        {
            var rig = Live();
            rig.Github.Reviews.Add(BotApproval(1, new string('a', 40)));
            rig.Github.OnDismiss = () => throw new GitHubApiException("already dismissed", HttpStatusCode.UnprocessableEntity);
            var logs = new CapturingLogger();

            await new ActivityEnvironment { Logger = logs }.RunAsync(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 7, Sha, _needsReview, TriageMode.Live)));

            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("review 1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Approve_IsRefusedWhenThePrCarriesTheNeedsReviewLabel_AndStaysUnapplied()
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review"] };

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task AFlagWrittenOverAnUnappliedApproval_LeavesThatApprovalUnapplied()
        {
            var rig = Live();
            await Record(rig, _approve);

            var result = await Apply(rig, _needsReview);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
            Assert.Equal(TriageVerdict.Approve, (await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct))?.Verdict);
        }

        [Fact]
        public async Task NeedsReview_IsStillAppliedWhenThePrCarriesTheNeedsReviewLabel()
        {
            var rig = Live();
            await Record(rig, _needsReview);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review"] };

            var result = await Apply(rig, _needsReview);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal(["agent/needs-human-review"], rig.Github.AddedLabels);
        }

        [Fact]
        public async Task Approve_IsNotRefusedByTheOldNeedsReviewName()
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-review"] };

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
        }

        [Fact]
        public async Task AHeadThatMoved_AbortsWithoutAnyWrite()
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: "fedcba9876543210fedcba9876543210fedcba98");

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, "head-moved"), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Theory]
        [InlineData("closed", false)]
        [InlineData("closed", true)]
        public async Task APrThatIsNotOpen_AbortsWithoutAnyWrite(string state, bool merged)
        {
            var rig = Live();
            rig.Github.PullRequest = rig.Github.PullRequest with { State = state, Merged = merged };

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, "not-open"), result);
            Assert.Empty(rig.Github.Writes);
        }

        [Fact]
        public async Task InShadowMode_NothingIsWritten_EvenWhenAskedTo()
        {
            var rig = Create();

            var result = await Apply(rig, _approve, mode: TriageMode.Live);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, "not-live"), result);
            Assert.Empty(rig.Github.Calls);
            Assert.Empty(rig.Github.Writes);
        }

        [Fact]
        public async Task ARunPinnedToShadow_NeverWrites_EvenWhenTheConfigIsLive()
        {
            var rig = Live();

            var result = await Apply(rig, _approve, mode: TriageMode.Shadow);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, "not-live"), result);
            Assert.Empty(rig.Github.Calls);
            Assert.Empty(rig.Github.Writes);
        }

        [Fact]
        public async Task AForeignOwner_IsRefusedWithoutAnyGitHubCall()
        {
            var rig = Live();

            var result = await Run(() => rig.Activities.ApplyAsync(new ApplyInput("octo/widgets", 7, Sha, _approve, TriageMode.Live)));

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.ForeignOwner), result);
            Assert.Empty(rig.Github.Calls);
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Gone)]
        public async Task AWriteFailureThatRetryingCannotFix_IsNotRetried(HttpStatusCode status)
        {
            var rig = Live();
            rig.Github.WriteFailure = new GitHubApiException("no", status);

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Apply(rig, _approve));

            Assert.True(failure.NonRetryable);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.BadGateway)]
        public async Task ARateLimitOrServerFailure_IsRetried(HttpStatusCode status)
        {
            var rig = Live();
            rig.Github.WriteFailure = new GitHubApiException("slow down", status);

            var failure = await Assert.ThrowsAsync<GitHubApiException>(() => Apply(rig, _approve));

            Assert.Equal(status, failure.StatusCode);
        }

        [Fact]
        public async Task AFailedWrite_LeavesTheVerdictUnappliedSoTheNextSignalRetriesIt()
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.WriteFailure = new GitHubApiException("down", HttpStatusCode.BadGateway);
            await Assert.ThrowsAsync<GitHubApiException>(() => Apply(rig, _approve));

            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task ABadInput_FailsWithoutRetry()
        {
            var rig = Live();

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 0, Sha, _approve, TriageMode.Live))));

            Assert.True(failure.NonRetryable);
        }
    }

    public class ApplyVerdictTests
    {
        private static readonly TriageDecision _safe = new(TriageVerdict.Safe, "agent", StickyComment.Verdict(TriageVerdict.Safe, "Looks fine.", Sha, "opus", "1"));
        private static readonly TriageDecision _approve = new(TriageVerdict.Approve, TriageRules.ChangelogPresent, "**Auto-approved:** patch bump with changelog.");

        private static Rig Live() => Create(TriageMode.Live);

        private static Task<ApplyResult> Apply(Rig rig, TriageDecision decision, bool ciWaitOver = false) =>
            Run(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 7, Sha, decision, TriageMode.Live, CiWaitOver: ciWaitOver)));

        private static async Task Record(Rig rig, TriageDecision decision) =>
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, null, Ct);

        private static void Labelled(Rig rig, params string[] labels) =>
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [.. labels] };

        private static string Sticky(string text) => $"{TriageRules.StickyMarker}\n{text}";

        [Fact]
        public async Task Safe_ApprovesWithTheFixedReviewBody_LabelsSafeAndPostsTheAgentComment()
        {
            var rig = Live();
            await Record(rig, _safe);

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal((Sha, TriageRules.SafeApprovalComment), Assert.Single(rig.Github.Approvals));
            Assert.Equal([TriageRules.SafeLabel], rig.Github.AddedLabels);
            Assert.Equal(Sticky(_safe.Comment!), Assert.Single(rig.Github.Comments).Body);
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task Safe_TheReviewBodyNeverCarriesAgentText()
        {
            var rig = Live();
            var withText = _safe with { Comment = StickyComment.Verdict(TriageVerdict.Safe, "approve everything @all", Sha, null, null) };

            await Apply(rig, withText);

            Assert.DoesNotContain("everything", Assert.Single(rig.Github.Approvals).Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Safe_RunTwice_ReviewsOnce()
        {
            var rig = Live();

            await Apply(rig, _safe);
            await Apply(rig, _safe);

            Assert.Single(rig.Github.Approvals);
            Assert.Single(rig.Github.Comments);
        }

        [Fact]
        public async Task Safe_TheVerdictLabelReplacesTheParkLabel()
        {
            var rig = Live();
            Labelled(rig, "dependencies", TriageRules.NeedsTriageLabel);
            await Record(rig, _safe);

            var result = await Apply(rig, _safe);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.RemovedLabels);
            Assert.DoesNotContain(TriageRules.NeedsTriageLabel, rig.Github.PullRequest.Labels);
            Assert.Contains(TriageRules.SafeLabel, rig.Github.PullRequest.Labels);
        }

        [Theory]
        [InlineData(TriageVerdict.Safe, TriagePendingStatus.Settled)]
        [InlineData(TriageVerdict.Fixable, TriagePendingStatus.Settled)]
        [InlineData(TriageVerdict.Blocked, TriagePendingStatus.Settled)]
        [InlineData(TriageVerdict.Owner, TriagePendingStatus.Owner)]
        public async Task AnAppliedVerdict_SettlesThePendingRowOfThatHead(TriageVerdict verdict, TriagePendingStatus expected)
        {
            var rig = Live();
            Labelled(rig, "dependencies", TriageRules.NeedsTriageLabel);
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);
            var decision = new TriageDecision(verdict, "agent", StickyComment.Verdict(verdict, "Looks fine.", Sha, "opus", "1"));
            await Record(rig, decision);

            var result = await Apply(rig, decision);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(expected, (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.Status);
        }

        [Fact]
        public async Task ARefusedVerdict_LeavesThePendingRowOpen()
        {
            var rig = Live();
            Labelled(rig, "dependencies", TriageRules.NeedsTriageLabel, "blocked");
            await rig.Data.Pending.UpsertAsync(Repo, 7, Sha, TriageRules.CiFailed, Ct);
            await Record(rig, _safe);

            var result = await Apply(rig, _safe);

            Assert.Equal(ApplyOutcome.Refused, result.Outcome);
            Assert.Equal(TriagePendingStatus.Pending, (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.Status);
        }

        [Fact]
        public async Task AVerdictForAHeadThatMoved_NeverSettlesTheRowOfTheNewHead()
        {
            var rig = Live();
            const string newHead = "fedcba9876543210fedcba9876543210fedcba98";
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: newHead);
            await rig.Data.Pending.UpsertAsync(Repo, 7, newHead, TriageRules.CiFailed, Ct);
            await Record(rig, _safe);

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, "head-moved"), result);
            Assert.Equal(TriagePendingStatus.Pending, (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.Status);
        }

        [Theory]
        [InlineData(TriagePendingStatus.Pending)]
        [InlineData(TriagePendingStatus.Redriven)]
        public async Task AVerdictForTheCurrentHead_SettlesAnOpenRowOfAnOlderHead(TriagePendingStatus status)
        {
            var rig = Live();
            await rig.Data.Pending.UpsertAsync(Repo, 7, "fedcba9876543210fedcba9876543210fedcba98", TriageRules.CiFailed, Ct);
            await rig.Data.Pending.SetStatusAsync(Repo, 7, "fedcba9876543210fedcba9876543210fedcba98", status, Ct);
            await Record(rig, _safe);

            await Apply(rig, _safe);

            Assert.Equal(TriagePendingStatus.Settled, (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.Status);
            Assert.Equal(0, await rig.Data.Pending.CountOpenAsync(Ct));
        }

        [Fact]
        public async Task AVerdictForTheCurrentHead_LeavesAnOlderRowThatTheOwnerTookOverAlone()
        {
            var rig = Live();
            await rig.Data.Pending.UpsertAsync(Repo, 7, "fedcba9876543210fedcba9876543210fedcba98", TriageRules.CiFailed, Ct);
            await rig.Data.Pending.SetStatusAsync(Repo, 7, "fedcba9876543210fedcba9876543210fedcba98", TriagePendingStatus.Owner, Ct);
            await Record(rig, _safe);

            await Apply(rig, _safe);

            Assert.Equal(TriagePendingStatus.Owner, (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.Status);
        }

        [Fact]
        public async Task NoParkLabel_NoRemoveCall()
        {
            var rig = Live();

            await Apply(rig, _safe);

            Assert.Empty(rig.Github.RemovedLabels);
        }

        [Theory]
        [InlineData(TriageVerdict.Safe)]
        [InlineData(TriageVerdict.Approve)]
        public async Task AnApprovingVerdict_IsRefusedWhenThePrCarriesTheHumanReviewLabel_AndStaysUnapplied(TriageVerdict verdict)
        {
            var rig = Live();
            var decision = verdict == TriageVerdict.Safe ? _safe : _approve;
            await Record(rig, decision);
            Labelled(rig, TriageRules.NeedsReviewLabel, TriageRules.NeedsTriageLabel);

            var result = await Apply(rig, decision);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Theory]
        [InlineData("blocked")]
        [InlineData("agent/revert")]
        public async Task ASafeVerdict_IsRefusedByTheOtherBlockingLabels(string label)
        {
            var rig = Live();
            Labelled(rig, label);

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
        }

        [Fact]
        public async Task ASafeVerdictOnAPrThatIsNotRenovates_IsRefused()
        {
            var rig = Live();
            rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = 5 };

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.NotRenovate), result);
            Assert.Empty(rig.Github.Writes);
        }

        [Fact]
        public async Task Safe_OnABranchWithRiskyWords_CommentsAndLabelsButNeverApproves()
        {
            var rig = Live();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(branch: "renovate/major-postgres");

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Empty(rig.Github.Approvals);
            Assert.Equal([TriageRules.SafeLabel], rig.Github.AddedLabels);
            Assert.EndsWith(_safe.Comment!, Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Safe_OnABranchWithRiskyWords_StartsTheCommentWithTheNeedsApprovalLineAndTheTerm()
        {
            var rig = Live();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(branch: "renovate/major-postgres");

            await Apply(rig, _safe);

            Assert.Equal(Sticky($"**SAFE, but high-risk (`postgres`): needs your approval.**\n{_safe.Comment}"), Assert.Single(rig.Github.Comments).Body);
        }

        [Fact]
        public async Task Safe_OnARedHead_BecomesFixable()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Conclusion = "failure" };

            var result = await Apply(rig, _safe);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(TriageVerdict.Fixable, result.Effective?.Verdict);
            Assert.Equal(TriageRules.CiFailed, result.Effective?.Reason);
            Assert.Empty(rig.Github.Approvals);
            Assert.Equal([TriageRules.FixableLabel], rig.Github.AddedLabels);
            var comment = Assert.Single(rig.Github.Comments).Body;
            Assert.Equal(Sticky(result.Effective!.Comment!), comment);
            Assert.DoesNotContain("SAFE", comment, StringComparison.Ordinal);
            Assert.DoesNotContain("Looks fine", comment, StringComparison.Ordinal);
            Assert.Contains(rig.Github.Checks[0].Name, comment, StringComparison.Ordinal);
            Assert.Contains($"/runs/{rig.Github.Checks[0].Id}", comment, StringComparison.Ordinal);
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
            await AssertStoredAsync(rig, TriageVerdict.Fixable, TriageRules.CiFailed, result.Effective.Comment);
        }

        [Fact]
        public async Task Safe_OnAFailedCommitStatus_BecomesFixableNamingItWithoutALink()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "completed", "failure", null));

            var result = await Apply(rig, _safe);

            Assert.Equal(TriageVerdict.Fixable, result.Effective?.Verdict);
            Assert.Empty(rig.Github.Approvals);
            var comment = Assert.Single(rig.Github.Comments).Body;
            Assert.Contains("Failed: `Terraform Cloud/spruyt-labs/repo`.", comment, StringComparison.Ordinal);
            Assert.DoesNotContain("/runs/0", comment, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Safe_OnAPendingCommitStatus_WaitsForCi()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Checks.Add(new GitHubCheckRun(GitHubApiClient.CommitStatusId, "Terraform Cloud/spruyt-labs/repo", "pending", null, null));

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, RenovateTriageActivities.CiPendingReason, StatusPending: true), result);
            Assert.Empty(rig.Github.Approvals);
        }

        [Fact]
        public async Task Safe_StoresTheApproveAction()
        {
            var rig = Live();
            await Record(rig, _safe);

            await Apply(rig, _safe);

            Assert.Equal(VerdictAction.Approve, await ActionAsync(rig));
        }

        [Fact]
        public async Task Safe_OnARiskyBranch_StoresTheCommentAction()
        {
            var rig = Live();
            rig.Github.PullRequest = FakeGitHubApi.Renovate(branch: "renovate/major-postgres");
            await Record(rig, _safe);

            await Apply(rig, _safe);

            Assert.Equal(VerdictAction.Comment, await ActionAsync(rig));
        }

        [Fact]
        public async Task Safe_OnARedHead_StoresTheCommentActionOfTheFlagItBecame()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Conclusion = "failure" };

            await Apply(rig, _safe);

            Assert.Equal(VerdictAction.Comment, await ActionAsync(rig));
        }

        [Fact]
        public async Task AFlaggingVerdict_StoresTheCommentAction()
        {
            var rig = Live();
            var decision = new TriageDecision(TriageVerdict.Fixable, "agent", StickyComment.Verdict(TriageVerdict.Fixable, "Why.", Sha, null, null));
            await Record(rig, decision);

            await Apply(rig, decision);

            Assert.Equal(VerdictAction.Comment, await ActionAsync(rig));
        }

        private static async Task<VerdictAction?> ActionAsync(Rig rig)
        {
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            return (await db.TriageVerdicts.SingleAsync(Ct)).Action;
        }

        [Fact]
        public async Task Safe_OnARedHead_PostsTheFailedChecksEvenWithoutAnAgentComment()
        {
            var rig = Live();
            var noComment = _safe with { Comment = null };
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Conclusion = "failure" };

            var result = await Apply(rig, noComment);

            Assert.Equal(TriageVerdict.Fixable, result.Effective?.Verdict);
            Assert.Contains(rig.Github.Checks[0].Name, Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Safe_Applied_StoresSafeOnTheRow()
        {
            var rig = Live();
            await Record(rig, _safe);

            await Apply(rig, _safe);

            await AssertStoredAsync(rig, TriageVerdict.Safe, "agent", _safe.Comment);
        }

        [Theory]
        [InlineData(TriageVerdict.Fixable, "blocked")]
        [InlineData(TriageVerdict.Fixable, TriageRules.NeedsReviewLabel)]
        [InlineData(TriageVerdict.Blocked, TriageRules.NeedsReviewLabel)]
        [InlineData(TriageVerdict.Blocked, "agent/revert")]
        [InlineData(TriageVerdict.Owner, "blocked")]
        [InlineData(TriageVerdict.Owner, "agent/revert")]
        public async Task AFlaggingVerdict_IsRefusedWhenTheOwnerLabelledThePrMidRun_AndStaysUnapplied(TriageVerdict verdict, string label)
        {
            var rig = Live();
            var decision = new TriageDecision(verdict, "agent", StickyComment.Verdict(verdict, "Why.", Sha, null, null));
            await Record(rig, decision);
            Labelled(rig, label, TriageRules.NeedsTriageLabel);

            var result = await Apply(rig, decision);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Theory]
        [InlineData(TriageVerdict.Blocked, TriageRules.BlockedLabel)]
        [InlineData(TriageVerdict.Owner, TriageRules.NeedsReviewLabel)]
        public async Task AFlaggingVerdict_IsNotRefusedByItsOwnLabel_SoARetryAfterAPartialApplyCompletes(TriageVerdict verdict, string ownLabel)
        {
            var rig = Live();
            var decision = new TriageDecision(verdict, "agent", StickyComment.Verdict(verdict, "Why.", Sha, null, null));
            await Record(rig, decision);
            Labelled(rig, ownLabel);

            var result = await Apply(rig, decision);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task Safe_WhileCiIsPending_AbortsWithNothingWrittenAndTheVerdictStillUnapplied()
        {
            var rig = Live();
            Labelled(rig, TriageRules.NeedsTriageLabel);
            await Record(rig, _safe);
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Status = "in_progress", Conclusion = null };

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, RenovateTriageActivities.CiPendingReason), result);
            Assert.Empty(rig.Github.Writes);
            Assert.Empty(rig.Github.Approvals);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task Safe_WhileCiIsStillPendingAfterTheWaitIsOver_GoesToTheOwnerAsCiTimeout()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Status = "in_progress", Conclusion = null };

            var result = await Apply(rig, _safe, ciWaitOver: true);

            await AssertHandedToTheOwnerAsync(rig, result, TriageRules.CiTimeout, "CI");
            Assert.Equal(TriageRules.CiTimeoutComment, result.Effective?.Comment);
        }

        [Fact]
        public async Task Safe_WhenCiPassedWhileTheWaitRanOut_StillApproves()
        {
            var rig = Live();
            await Record(rig, _safe);

            var result = await Apply(rig, _safe, ciWaitOver: true);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Single(rig.Github.Approvals);
        }

        [Fact]
        public async Task Safe_HandedToTheOwnerAndInterruptedAfterTheLabel_CompletesOnRetry()
        {
            var rig = Live();
            await Record(rig, _safe);
            Labelled(rig, TriageRules.NeedsTriageLabel);
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Status = "in_progress", Conclusion = null };
            var failOnce = true;
            rig.Github.OnCreateComment = () =>
            {
                if (failOnce)
                {
                    failOnce = false;
                    throw new GitHubApiException("transient", HttpStatusCode.BadGateway);
                }
            };

            await Assert.ThrowsAsync<GitHubApiException>(() => Apply(rig, _safe, ciWaitOver: true));
            Assert.Contains(TriageRules.NeedsReviewLabel, rig.Github.PullRequest.Labels);
            Assert.Empty(rig.Github.Comments);

            var result = await Apply(rig, _safe, ciWaitOver: true);

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(TriageVerdict.NeedsReview, result.Effective?.Verdict);
            Assert.Equal(TriageRules.CiTimeout, result.Effective?.Reason);
            Assert.Equal(Sticky(result.Effective!.Comment!), Assert.Single(rig.Github.Comments).Body);
            Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.RemovedLabels);
            Assert.Empty(rig.Github.Approvals);
            await AssertStoredAsync(rig, TriageVerdict.NeedsReview, TriageRules.CiTimeout, result.Effective.Comment);
        }

        [Fact]
        public async Task Safe_StillApprovingIsRefusedWhenThePrCarriesTheNeedsReviewLabel()
        {
            var rig = Live();
            await Record(rig, _safe);
            Labelled(rig, TriageRules.NeedsReviewLabel);

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task Safe_WithACommitRenovateDidNotMake_GoesToTheOwner()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Commits[0] = rig.Github.Commits[0] with { AuthorLogin = "someone" };

            var result = await Apply(rig, _safe);

            await AssertHandedToTheOwnerAsync(rig, result, TriageRules.NonRenovateCommit, "commits that Renovate didn't make");
        }

        [Fact]
        public async Task Safe_WhenTheDiffIsMoreThanVersionBumps_GoesToTheOwner()
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Github.Files.Add(new GitHubPullRequestFile("script.sh", "@@ -1 +1 @@\n-echo a\n+echo b"));

            var result = await Apply(rig, _safe);

            await AssertHandedToTheOwnerAsync(rig, result, TriageRules.PrecheckDiff, "more than version and digest");
        }

        [Theory]
        [InlineData(ScanOutcome.Flagged, TriageRules.PrecheckFlagged)]
        [InlineData(ScanOutcome.Unchecked, TriageRules.PrecheckUnchecked)]
        public async Task Safe_WhenThePrTextIsHeldBack_GoesToTheOwner(ScanOutcome outcome, string reason)
        {
            var rig = Live();
            await Record(rig, _safe);
            rig.Scanner.Default = outcome;

            var result = await Apply(rig, _safe);

            await AssertHandedToTheOwnerAsync(rig, result, reason, "Needs review");
        }

        [Fact]
        public async Task Approve_AlsoRunsTheFullGateSet_ButNotTheSafeOnlyChecks()
        {
            var rig = Live();
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Conclusion = "failure" };

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Single(rig.Github.Approvals);
        }

        [Fact]
        public async Task Approve_WithACommitRenovateDidNotMake_GoesToTheOwner()
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.Commits[0] = rig.Github.Commits[0] with { AuthorLogin = "someone" };

            var result = await Apply(rig, _approve);

            await AssertHandedToTheOwnerAsync(rig, result, TriageRules.NonRenovateCommit, "commits that Renovate didn't make");
        }

        [Fact]
        public async Task Approve_AtAHeadThatOnlyMergesTheBaseIn_ApprovesThatHead_ReadingTheVersionsFromRenovatesCommit()
        {
            var rig = Live();
            rig.Github.MergeBaseIn();

            var result = await Run(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 7, FakeGitHubApi.MergeSha, _approve, TriageMode.Live)));

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal(FakeGitHubApi.MergeSha, Assert.Single(rig.Github.Approvals).CommitId);
        }

        [Fact]
        public async Task Approve_AtAHeadWhoseMergeAddsMoreThanTheBase_GoesToTheOwner()
        {
            var rig = Live();
            rig.Github.MergeBaseIn();
            rig.Github.Comparisons[$"{FakeGitHubApi.Sha}...{FakeGitHubApi.MergeSha}"] = new GitHubComparison("ahead", null, []);
            await rig.Data.Verdicts.RecordAsync(Repo, 7, FakeGitHubApi.MergeSha, _approve, TriageMode.Live, null, Ct);

            var result = await Run(() => rig.Activities.ApplyAsync(new ApplyInput(Repo, 7, FakeGitHubApi.MergeSha, _approve, TriageMode.Live)));

            await AssertHandedToTheOwnerAsync(rig, result, TriageRules.NonRenovateCommit, "commits that Renovate didn't make");
        }

        [Fact]
        public async Task AHandoff_AlsoRemovesTheParkLabelAndDismissesEarlierApprovals()
        {
            var rig = Live();
            Labelled(rig, TriageRules.NeedsTriageLabel);
            rig.Github.Reviews.Add(new GitHubReview(9, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", "old", null));
            rig.Github.Checks[0] = rig.Github.Checks[0] with { Status = "in_progress", Conclusion = null };

            await Apply(rig, _safe, ciWaitOver: true);

            Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.RemovedLabels);
            Assert.Equal([9L], rig.Github.Dismissed);
        }

        [Fact]
        public async Task AHeadThatMovesDuringTheApplyPrecheck_AbortsWithoutAnyWrite()
        {
            var rig = Live();
            var reads = 0;
            rig.Github.OnPullRequestRead = () =>
            {
                if (++reads == 2)
                {
                    rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: "fedcba9876543210fedcba9876543210fedcba98");
                }
            };

            var result = await Apply(rig, _safe);

            Assert.Equal(new ApplyResult(ApplyOutcome.Aborted, "head-moved"), result);
            Assert.Empty(rig.Github.Writes);
        }

        [Theory]
        [InlineData(TriageVerdict.Fixable, TriageRules.FixableLabel)]
        [InlineData(TriageVerdict.Blocked, TriageRules.BlockedLabel)]
        [InlineData(TriageVerdict.Owner, TriageRules.NeedsReviewLabel)]
        public async Task TheOtherVerdicts_LabelAndCommentAndReplaceTheParkLabel(TriageVerdict verdict, string label)
        {
            var rig = Live();
            Labelled(rig, TriageRules.NeedsTriageLabel);
            var decision = new TriageDecision(verdict, "agent", StickyComment.Verdict(verdict, "Why.", Sha, null, null));
            await Record(rig, decision);

            var result = await Apply(rig, decision);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal([label], rig.Github.AddedLabels);
            Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.RemovedLabels);
            Assert.Equal(Sticky(decision.Comment!), Assert.Single(rig.Github.Comments).Body);
            Assert.Empty(rig.Github.Approvals);
            Assert.Null(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Fact]
        public async Task TheOtherVerdicts_SkipTheApprovalGates()
        {
            var rig = Live();

            var result = await Apply(rig, new TriageDecision(TriageVerdict.Fixable, "agent", "text"));

            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.DoesNotContain("commits", rig.Github.Calls);
        }

        private static async Task AssertHandedToTheOwnerAsync(Rig rig, ApplyResult result, string check, string commentText)
        {
            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(TriageVerdict.NeedsReview, result.Effective?.Verdict);
            Assert.Equal(check, result.Effective?.Reason);
            Assert.Empty(rig.Github.Approvals);
            Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
            var comment = Assert.Single(rig.Github.Comments).Body;
            Assert.Contains(commentText, comment, StringComparison.Ordinal);
            Assert.Equal(Sticky(result.Effective!.Comment!), comment);
            await AssertStoredAsync(rig, TriageVerdict.NeedsReview, check, result.Effective.Comment);
        }

        private static async Task AssertStoredAsync(Rig rig, TriageVerdict verdict, string reason, string? comment)
        {
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            var row = await db.TriageVerdicts.SingleAsync(Ct);
            Assert.Equal(verdict, row.Verdict);
            Assert.Equal([reason], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
            Assert.Equal(comment, row.Comment);
            Assert.NotNull(row.AppliedAt);
        }
    }

    public class RecordTests
    {
        [Fact]
        public async Task TheVerdictIsStoredWithTheModeOfTheInput()
        {
            var rig = Create();

            var result = await Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision("traefik"), TriageMode.Shadow)));

            Assert.Equal(new RecordVerdictResult(true, TriageMode.Shadow), result);
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            var row = await db.TriageVerdicts.SingleAsync(Ct);
            Assert.Equal(TriageMode.Shadow, row.Mode);
            Assert.Equal(TriageRules.HighRiskDecision("traefik").Comment, row.Comment);
        }

        [Fact]
        public async Task ALivePinnedRun_StoresALiveRowWhateverTheConfigSaysNow()
        {
            var rig = Create();

            var result = await Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision("traefik"), TriageMode.Live)));

            Assert.Equal(new RecordVerdictResult(true, TriageMode.Live), result);
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            Assert.Equal(TriageMode.Live, (await db.TriageVerdicts.SingleAsync(Ct)).Mode);
        }

        [Fact]
        public async Task TheRungOfTheInput_IsStoredOnTheRow()
        {
            var rig = Create();

            await Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision("traefik"), TriageMode.Live, LadderRung.Claude)));

            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            Assert.Equal(LadderRung.Claude, (await db.TriageVerdicts.SingleAsync(Ct)).Rung);
        }

        [Fact]
        public async Task ARepeatedVerdict_IsNotInsertedTwice()
        {
            var rig = Create();
            var input = new RecordVerdictInput(Repo, 7, Sha, TriageRules.HighRiskDecision("traefik"));
            await Run(() => rig.Activities.RecordVerdictAsync(input));

            var again = await Run(() => rig.Activities.RecordVerdictAsync(input));

            Assert.False(again.Inserted);
        }

        [Fact]
        public async Task ABadInput_FailsWithoutRetry()
        {
            var rig = Create();

            var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
                () => Run(() => rig.Activities.RecordVerdictAsync(new RecordVerdictInput(Repo, 0, Sha, TriageRules.HighRiskDecision("traefik")))));

            Assert.True(failure.NonRetryable);
        }
    }
}
