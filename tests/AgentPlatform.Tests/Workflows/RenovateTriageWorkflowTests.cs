using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Workflows;

public class RenovateTriageWorkflowTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string WorkflowId = "renovate-triage/anthony-spruyt/widgets/7";
    private const string ShaA = FakeGitHubApi.Sha;
    private const string ShaB = "fedcba9876543210fedcba9876543210fedcba98";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public FakeGitHubApi Github { get; } = new();

        public FakeJev Jev { get; } = new();

        public InMemoryRunData Data { get; } = InMemoryRunData.Create();

        public TriageMode Mode { get; init; } = TriageMode.Shadow;
    }

    private static CheckSuiteCompletedSignal Signal(string sha = ShaA, long suite = 1) =>
        new(Repo, 7, sha, suite, "github-actions", "success", $"d-{suite}");

    private static Task<T> InWorkerAsync<T>(WorkflowEnvironment env, Rig rig, Func<Task<T>> body) =>
        InWorkerAsync(env, rig, new RenovateTriageOptions { Mode = rig.Mode }, body);

    private static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, Rig rig, RenovateTriageOptions options, Func<Task<T>> body)
    {
        var triage = new RenovateTriageActivities(rig.Github, rig.Jev, rig.Data.Verdicts, Options.Create(options));
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<RenovateTriageWorkflow>()
                .AddAllActivities(triage)
                .AddAllActivities(rig.Data.Activities));
        return await worker.ExecuteAsync(body, Ct);
    }

    private static Task SignalAsync(WorkflowEnvironment env, CheckSuiteCompletedSignal signal) =>
        new TemporalRenovateTriageSignaler(env.Client).SignalAsync(signal, Ct);

    // Only handles returned by StartWorkflowAsync skip time while awaiting a result, so the first signal starts the workflow here with the signaler's options.
    private static Task<WorkflowHandle<RenovateTriageWorkflow, RenovateTriageResult>> StartAsync(WorkflowEnvironment env, CheckSuiteCompletedSignal signal) =>
        env.Client.StartWorkflowAsync(
            (RenovateTriageWorkflow wf) => wf.RunAsync(null),
            new WorkflowOptions(RenovateTriageWorkflow.WorkflowId(signal.Repo, signal.PrNumber), TaskQueues.AgentPlatform)
            {
                StartSignal = RenovateTriageWorkflow.CheckSuiteCompletedSignalName,
                StartSignalArgs = [signal],
            });

    private static async Task<RenovateTriageResult> RunToResultAsync(WorkflowHandle<RenovateTriageWorkflow, RenovateTriageResult> handle, string? record = null)
    {
        var result = await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct });
        if (record is not null)
        {
            await HistoryStore.RecordAsync(handle, record);
        }

        return result;
    }

    private static WorkflowHandle Handle(WorkflowEnvironment env, string id, string? runId) =>
        env.Client.GetWorkflowHandle(id, runId);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the workflow.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<List<RenovateTriageVerdict>> VerdictsAsync(Rig rig)
    {
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        return await db.TriageVerdicts.OrderBy(v => v.CreatedAt).ToListAsync(Ct);
    }

    [Fact]
    public async Task ACleanRenovateBump_IsApprovedInShadowMode_AndOnlyRecorded()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-approve");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ChangelogPresent, TriageVerdict.Approve), result);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((Repo, 7, ShaA), (verdict.Repo, verdict.PrNumber, verdict.HeadSha));
        Assert.Equal(TriageVerdict.Approve, verdict.Verdict);
        Assert.Equal(TriageMode.Shadow, verdict.Mode);
        Assert.Equal("**Auto-approved:** patch bump with changelog.", verdict.Comment);
    }

    [Fact]
    public async Task ARunIsRecordedWithItsStepsAndTheJevAnswers()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(Ct);
        Assert.Equal("RenovateTriageWorkflow", run.WorkflowType);
        Assert.Equal(WorkflowId, run.WorkflowId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["assess", "jev", "verdict"], run.Steps.OrderBy(s => s.Seq).Select(s => s.Name));
        Assert.Equal([LadderRung.Rule, LadderRung.Jev, LadderRung.Rule], run.Steps.OrderBy(s => s.Seq).Select(s => s.Rung));
        Assert.Contains("\"changelog\":0.78", run.Steps.Single(s => s.Name == "jev").Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCheck_IsNeedsReviewWithTheCiComment_AndJevIsNotAsked()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-ci-failed");
        });

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageRules.CiFailedDecision(Repo, ShaA, [rig.Github.Checks[1]]).Comment, verdict.Comment);
        Assert.Empty(rig.Jev.Bodies);
    }

    [Fact]
    public async Task ARiskyBranch_IsNeedsReviewWithTheHighRiskComment_AndJevIsNotAsked()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.PullRequest = FakeGitHubApi.Renovate("renovate/cilium-1.x");

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageRules.HighRiskDecision("cilium").Comment, Assert.Single(await VerdictsAsync(rig)).Comment);
        Assert.Empty(rig.Jev.Bodies);
    }

    [Fact]
    public async Task AJevError_IsNeedsReviewWithTheJevErrorComment()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Failure = new JevException("Jev answered with status 502.");

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-jev-error");
        });

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        Assert.Equal(TriageRules.JevErrorComment, Assert.Single(await VerdictsAsync(rig)).Comment);
    }

    [Fact]
    public async Task APrNotFromRenovate_IsSkippedWithoutAVerdict()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = 5 };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-skipped");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Skipped, ShaA, TriageRules.NotRenovate, null), result);
        Assert.Empty(await VerdictsAsync(rig));
        Assert.Empty(rig.Jev.Bodies);
    }

    [Fact]
    public async Task ACommitThatWasJudgedBefore_IsNotJudgedAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, TriageRules.CiFailedDecision(Repo, ShaA, []), TriageMode.Shadow, Ct);

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        Assert.Equal(RenovateTriageOutcome.AlreadyJudged, result.Outcome);
        Assert.Single(await VerdictsAsync(rig));
        Assert.Empty(rig.Jev.Bodies);
    }

    [Fact]
    public async Task PendingChecks_WaitForTheNextSignal_ThenJudge()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.Github.Calls.Any(c => c.StartsWith("checks", StringComparison.Ordinal)));
            Assert.Empty(await VerdictsAsync(rig));

            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle, "renovate-triage-pending-then-approve");
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Single(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task ChecksStillPendingAfterTwoHours_AreNeedsReview()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-ci-timeout");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.CiTimeout, TriageVerdict.NeedsReview), result);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageRules.CiTimeoutComment, verdict.Comment);
        Assert.Empty(rig.Jev.Bodies);
        Assert.Equal(2, rig.Github.Calls.Count(c => c.StartsWith("checks", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ChecksThatFinishedWithoutASignal_AreJudgedWhenTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Calls.Any(c => c.StartsWith("checks", StringComparison.Ordinal)));
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            return await RunToResultAsync(handle, "renovate-triage-ci-timeout-reassessed");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ChangelogPresent, TriageVerdict.Approve), result);
    }

    [Fact]
    public async Task APrApprovedWhileWaiting_IsSkippedWhenTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Calls.Any(c => c.StartsWith("checks", StringComparison.Ordinal)));
            rig.Github.Reviews.Add(new GitHubReview(1, "owner", "OWNER", "APPROVED", ShaA, null));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Skipped, ShaA, TriageRules.AlreadyApproved, null), result);
        Assert.Empty(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task ACommitNotMadeByRenovate_IsNeedsReviewWithoutWaitingForChecksOrAskingJev()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Commits.Add(rig.Github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 });
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-commit-gate");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.NonRenovateCommit, TriageVerdict.NeedsReview), result);
        Assert.Equal(TriageRules.NonRenovateCommitComment, Assert.Single(await VerdictsAsync(rig)).Comment);
        Assert.Empty(rig.Jev.Bodies);
    }

    [Fact]
    public async Task AMajorBumpAnsweredByJev_IsNeedsReview()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Answers = new JevAnswers(0.04, "major", 0.9);

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-major-bump");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.MajorBump, TriageVerdict.NeedsReview), result);
        Assert.Single(rig.Jev.Bodies);
        Assert.Single(rig.Jev.Diffs);
    }

    [Fact]
    public async Task APrWithoutAnUpdateTable_IsJudgedByJevAlone()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.PullRequest = rig.Github.PullRequest with { Body = "no table" };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageRules.ChangelogPresent, result.Reason);
        Assert.Equal(["no table"], rig.Jev.Bodies);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(Ct);
        Assert.Equal("answered", run.Steps.Single(s => s.Name == "jev").StopReason);
    }

    [Fact]
    public async Task ASuiteThatIsStillRunning_HoldsTheJudgementUntilItCompletes()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Suites.Add(new GitHubCheckSuite(2, "queued", null, "github-actions", 0));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.Github.Calls.Any(c => c.StartsWith("suites", StringComparison.Ordinal)));
            Assert.Empty(await VerdictsAsync(rig));

            rig.Github.Suites[1] = new GitHubCheckSuite(2, "completed", "success", "github-actions", 3);
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle, "renovate-triage-suite-pending");
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Single(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task ANewHeadCommit_RestartsTheJudging()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.Github.Calls.Any(c => c.StartsWith("checks", StringComparison.Ordinal)));

            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            rig.Github.Checks.Clear();
            rig.Github.Checks.Add(new GitHubCheckRun(3, "test", "completed", "success", "github-actions"));
            await SignalAsync(env, Signal(ShaB, suite: 2));
            var first = Handle(env, handle.Id, handle.ResultRunId);
            var latest = await RunToResultAsync(handle, "renovate-triage-new-head");
            await HistoryStore.RecordAsync(first, "renovate-triage-new-head-superseded");
            return latest;
        });

        Assert.Equal(ShaB, result.HeadSha);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(ShaB, verdict.HeadSha);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(2, await db.Runs.CountAsync(Ct));
    }

    [Fact]
    public async Task TheHeadCommitMovingDuringTheJevCall_RestartsTheJudging()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        var moved = false;
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == 2 && !moved)
            {
                moved = true;
                rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            }
        };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        Assert.Equal(ShaB, result.HeadSha);
        Assert.Equal(ShaB, Assert.Single(await VerdictsAsync(rig)).HeadSha);
    }

    [Fact]
    public async Task AnUnreadablePr_FailsTheWorkflowAndTheRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Github = { Failure = new GitHubApiException("not found", System.Net.HttpStatusCode.NotFound) } };

        var failure = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await Assert.ThrowsAsync<WorkflowFailedException>(() => RunToResultAsync(handle));
        });

        Assert.IsType<ActivityFailureException>(failure.InnerException);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        Assert.Empty(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task EachSignalIsOnlyATrigger_TheSignalContentIsNeverJudged()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle);
        });

        Assert.Equal(ShaB, result.HeadSha);
        Assert.Equal(ShaB, Assert.Single(await VerdictsAsync(rig)).HeadSha);
    }

    [Fact]
    public async Task ShadowMode_NeverWritesToGitHub()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var approved = new Rig();
        var flagged = new Rig();
        flagged.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

        await InWorkerAsync(env, approved, async () => await RunToResultAsync(await StartAsync(env, Signal())));
        await InWorkerAsync(env, flagged, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.Approve, Assert.Single(await VerdictsAsync(approved)).Verdict);
        Assert.Equal(TriageVerdict.NeedsReview, Assert.Single(await VerdictsAsync(flagged)).Verdict);
        Assert.Empty(approved.Github.Writes);
        Assert.Empty(flagged.Github.Writes);
        Assert.All(await VerdictsAsync(approved), v => Assert.Null(v.AppliedAt));
    }

    [Fact]
    public async Task LiveMode_ApprovesTheJudgedCommit_LabelsIt_AndMarksTheVerdictApplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-live-approve");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ChangelogPresent, TriageVerdict.Approve), result);
        Assert.Equal([(ShaA, "**Auto-approved:** patch bump with changelog.")], rig.Github.Approvals);
        Assert.Equal([TriageRules.AutoApprovedLabel], rig.Github.AddedLabels);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageMode.Live, verdict.Mode);
        Assert.NotNull(verdict.AppliedAt);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(Ct);
        Assert.Equal(["assess", "jev", "verdict", "apply"], run.Steps.OrderBy(s => s.Seq).Select(s => s.Name));
    }

    [Fact]
    public async Task LiveMode_FlagsAFailedPr_WithTheNeedsReviewLabelAndAStickyComment()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-live-needs-review");
        });

        Assert.Empty(rig.Github.Approvals);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        var comment = Assert.Single(rig.Github.Comments);
        Assert.StartsWith(TriageRules.StickyMarker, comment.Body, StringComparison.Ordinal);
        Assert.Contains("CI failed on `0123456`", comment.Body, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
    }

    [Fact]
    public async Task LiveMode_WhenTheHeadMovedBeforeTheWrite_WritesNothingAndLeavesTheVerdictUnapplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == 2)
            {
                rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            }
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(RenovateTriageOutcome.Judged, result.Outcome);
        Assert.Empty(rig.Github.Writes);
        Assert.Null(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
    }

    [Fact]
    public async Task LiveMode_AVerdictThatWasNotApplied_IsAppliedOnTheNextSignal()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.WriteFailure = new GitHubApiException("unprocessable", System.Net.HttpStatusCode.UnprocessableEntity);

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await Assert.ThrowsAsync<WorkflowFailedException>(() => RunToResultAsync(handle));
            Assert.Null(Assert.Single(await VerdictsAsync(rig)).AppliedAt);

            rig.Github.WriteFailure = null;
            var second = await StartAsync(env, Signal(suite: 2));
            return await RunToResultAsync(second);
        });

        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.NotNull(verdict.AppliedAt);
        Assert.Equal([ShaA], rig.Github.Approvals.Select(a => a.CommitId));
        Assert.Single(rig.Jev.Bodies);
    }

    [Fact]
    public async Task AnAppliedLiveVerdict_IsNotWrittenAgainOnALaterSignal()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            await RunToResultAsync(await StartAsync(env, Signal()));
            rig.Github.Writes.Clear();
            return await RunToResultAsync(await StartAsync(env, Signal(suite: 2)));
        });

        Assert.Empty(rig.Github.Writes);
        Assert.Single(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task TheModeIsPinnedAtAssess_ALaterSwitchToLiveDoesNotWriteInThatRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Shadow };
        var options = new RenovateTriageOptions { Mode = TriageMode.Shadow };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));
        rig.Github.OnPullRequestRead = () => options.Mode = TriageMode.Live;

        var result = await InWorkerAsync(env, rig, options, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(RenovateTriageOutcome.Judged, result.Outcome);
        Assert.Empty(rig.Github.Writes);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageMode.Shadow, verdict.Mode);
        Assert.Null(verdict.AppliedAt);
    }

    [Fact]
    public async Task TheModeIsPinnedAtAssess_ALaterSwitchToShadowDoesNotWriteInThatRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        var options = new RenovateTriageOptions { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "completed", "failure", "github-actions"));
        rig.Github.OnPullRequestRead = () => options.Mode = TriageMode.Shadow;

        await InWorkerAsync(env, rig, options, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Empty(rig.Github.Writes);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageMode.Live, verdict.Mode);
        Assert.Null(verdict.AppliedAt);
    }

    [Fact]
    public async Task AJevError_IsRetriedThreeTimes_ThenBecomesNeedsReview()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Failure = new JevException("Jev answered with status 502.");

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-jev-error-retried");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.JevError, TriageVerdict.NeedsReview), result);
        Assert.Equal(3, rig.Jev.Bodies.Count);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(Ct);
        Assert.Equal("error", run.Steps.Single(s => s.Name == "jev").StopReason);
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task AJevErrorThatClears_IsJudgedOnTheRetry()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Failure = new JevException("Jev answered with status 502.");
        rig.Jev.FailuresBeforeAnswer = 1;

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.ChangelogPresent, result.Reason);
        Assert.Equal(2, rig.Jev.Bodies.Count);
    }

}
