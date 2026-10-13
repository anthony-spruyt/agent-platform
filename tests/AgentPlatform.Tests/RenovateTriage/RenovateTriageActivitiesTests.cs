using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
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
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["agent/needs-human-review", "agent/needs-review", label] };

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel), await Assess(rig));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
        [InlineData("agent/auto-approved")]
        public async Task InShadowMode_ALabelFromTheLivePath_DoesNotSkipThePr(string label)
        {
            var rig = Create();
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(new AssessResult(AssessStatus.NeedsJev, Sha), await Assess(rig));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
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
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, Ct);
            await Assess(rig);
            Assert.DoesNotContain("commits", rig.Github.Calls);
        }

        [Fact]
        public async Task ACommitThatHasAVerdict_IsAlreadyJudged_WithoutReadingTheChecks()
        {
            var rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, Ct);

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
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);
            await rig.Data.Verdicts.MarkAppliedAsync(Repo, 7, Sha, Ct);

            Assert.Equal(new AssessResult(AssessStatus.AlreadyJudged, Sha, Mode: TriageMode.Live), await Assess(rig));
        }

        [Fact]
        public async Task AShadowVerdict_DoesNotCountAsJudgedInLive()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Shadow, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task ALiveVerdictDoesNotCountAsJudgedInShadow()
        {
            var rig = Create();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);

            Assert.Equal(AssessStatus.NeedsJev, (await Assess(rig)).Status);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsDecidedAgainFromTheStoredRow()
        {
            var rig = Live();
            var decision = TriageRules.CiFailedDecision(Repo, Sha, [new GitHubCheckRun(3, "build", "completed", "failure", "github-actions")]);
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, Ct);

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Decided, Sha, Decision: decision, Mode: TriageMode.Live), result);
            Assert.DoesNotContain(rig.Github.Calls, c => c.StartsWith("checks", StringComparison.Ordinal));
            Assert.Empty(rig.Jev.Bodies);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsReappliedDespiteTheLabelAndApprovalOfItsOwnWrites()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), TriageMode.Live, Ct);
            rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", Sha, null));
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.AutoApprovedLabel] };

            Assert.Equal(AssessStatus.Decided, (await Assess(rig)).Status);
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
        public async Task AnUnappliedNeedsReviewVerdict_IsReappliedDespiteTheNeedsReviewLabelOfItsOwnWrites(string label)
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(AssessStatus.Decided, (await Assess(rig)).Status);
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
        public async Task AnUnappliedApproveVerdict_IsStillHeldBackByAHumansNeedsReviewLabel(string label)
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), TriageMode.Live, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            var result = await Assess(rig);

            Assert.Equal(new AssessResult(AssessStatus.Skipped, Sha, TriageRules.BlockingLabel, Mode: TriageMode.Live), result);
        }

        [Theory]
        [InlineData("blocked")]
        [InlineData("agent/revert")]
        public async Task AnUnappliedLiveVerdict_IsStillHeldBackByAHumansBlockingLabel(string label)
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            Assert.Equal(TriageRules.BlockingLabel, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task AnUnappliedLiveVerdict_IsStillHeldBackByAHumanApproval()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", Sha, null));

            Assert.Equal(TriageRules.AlreadyApproved, (await Assess(rig)).Reason);
        }

        [Fact]
        public async Task AnUnappliedVerdictOfAnEarlierCommit_DoesNotShortcutTheNewOne()
        {
            var rig = Live();
            await rig.Data.Verdicts.RecordAsync(Repo, 7, new string('a', 40), TriageRules.CiFailedDecision(Repo, Sha, []), TriageMode.Live, Ct);

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
            await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, decision, TriageMode.Live, Ct);

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

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
        public async Task Approve_IsRefusedWhenThePrCarriesEitherNeedsReviewName_AndStaysUnapplied(string label)
        {
            var rig = Live();
            await Record(rig, _approve);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            var result = await Apply(rig, _approve);

            Assert.Equal(new ApplyResult(ApplyOutcome.Refused, TriageRules.BlockingLabel), result);
            Assert.Empty(rig.Github.Writes);
            Assert.NotNull(await rig.Data.Verdicts.FindUnappliedAsync(Repo, 7, Sha, Ct));
        }

        [Theory]
        [InlineData("agent/needs-human-review")]
        [InlineData("agent/needs-review")]
        public async Task NeedsReview_IsStillAppliedWhenThePrCarriesEitherNeedsReviewName(string label)
        {
            var rig = Live();
            await Record(rig, _needsReview);
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [label] };

            var result = await Apply(rig, _needsReview);

            Assert.Equal(new ApplyResult(ApplyOutcome.Applied), result);
            Assert.Equal(["agent/needs-human-review"], rig.Github.AddedLabels);
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
