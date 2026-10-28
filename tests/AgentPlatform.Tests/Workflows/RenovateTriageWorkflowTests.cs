using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Workflows;

public partial class RenovateTriageWorkflowTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string WorkflowId = "renovate-triage/anthony-spruyt/widgets/7";
    private const string ShaA = FakeGitHubApi.Sha;
    private const string ShaB = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Model = "opus-test";

    // Pull request reads before the classify step and before the apply step: assess, precheck (two), migration, assess, then the step itself.
    private const int ClassifyRead = 6;
    private const int ApplyRead = 7;
    private const int DiffApplyRead = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public FakeGitHubApi Github { get; } = new();

        public FakeJev Jev { get; } = new();

        public InMemoryRunData Data { get; } = InMemoryRunData.Create();

        public FakeTextScanner Scanner { get; } = new();

        public FakeCheckRunReporter CheckRuns { get; } = new();

        public TriageMode Mode { get; init; } = TriageMode.Shadow;

        public ScriptedBudget Budget { get; } = new();

        public ScriptedPods Pods { get; } = new();

        public RecordingCleanup Cleanup { get; } = new();
    }

    private static CheckSuiteCompletedSignal Signal(string sha = ShaA, long suite = 1) =>
        new(Repo, 7, sha, suite, "github-actions", "success", $"d-{suite}");

    private static Task<T> InWorkerAsync<T>(WorkflowEnvironment env, Rig rig, Func<Task<T>> body) =>
        InWorkerAsync(env, rig, new RenovateTriageOptions { Mode = rig.Mode }, body);

    private static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, Rig rig, RenovateTriageOptions options, Func<Task<T>> body)
    {
        var triage = new RenovateTriageActivities(rig.Github, rig.Jev, rig.Data.Verdicts, rig.Data.Pending, Options.Create(options), rig.Scanner);
        var code = new RenovateTriageCodeActivities(rig.Github, rig.Jev, rig.Scanner, Options.Create(options));
        var checks = new TriageCheckActivities(rig.CheckRuns);
        var claude = new ClaudeRungActivities(rig.Github, rig.Data.Pending, new AgentPodsOptions { Model = Model }, Options.Create(options));
        var prompts = new PromptActivities(new AgentPromptsOptions(), NullLogger<PromptActivities>.Instance);
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<RenovateTriageWorkflow>()
                .AddAllActivities(triage)
                .AddAllActivities(code)
                .AddAllActivities(checks)
                .AddAllActivities(claude)
                .AddAllActivities(prompts)
                .AddAllActivities(rig.Budget)
                .AddAllActivities(rig.Cleanup)
                .AddAllActivities(rig.Data.Activities));
        using var pods = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(rig.Pods));
        return await worker.ExecuteAsync(() => pods.ExecuteAsync(body, Ct), Ct);
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
        Assert.Equal("**Auto-approved:** patch bump, Jev scored the changelog 0.78.", verdict.Comment);
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
        Assert.Equal(["assess", "precheck", "migration", "assess", "jev", "verdict"], run.Steps.OrderBy(s => s.Seq).Select(s => s.Name));
        Assert.Equal([LadderRung.Rule, LadderRung.Rule, LadderRung.Rule, LadderRung.Rule, LadderRung.Jev, LadderRung.Rule], run.Steps.OrderBy(s => s.Seq).Select(s => s.Rung));
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
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, TriageRules.CiFailedDecision(Repo, ShaA, []), TriageMode.Shadow, null, Ct);

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
    }

    [Fact]
    public async Task ChecksThatFinishWithoutASignal_AreJudgedOnTheNextPoll_NotWhenTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForStepAsync(rig, "assess", "Pending");
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            return await RunToResultAsync(handle, "renovate-triage-ci-poll-pending");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ChangelogPresent, TriageVerdict.Approve), result);
        var steps = await StepsAsync(rig);
        var waitedAt = steps.First(s => s is { Name: "assess", StopReason: "Pending" }).StartedAt;
        var judgedAt = steps.Last(s => s.Name == "assess").StartedAt;
        Assert.True(judgedAt - waitedAt < 2 * RenovateTriageWorkflow.StatusPollInterval, $"Judged after {judgedAt - waitedAt}.");
    }

    [Fact]
    public async Task ACommitStatusThatCompletesWithoutASignal_IsJudgedOnTheStatusPoll_NotWhenTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(CommitStatus("pending", null));

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForStepAsync(rig, "assess", "Pending");
            rig.Github.Checks[1] = CommitStatus("completed", "success");
            return await RunToResultAsync(handle, "renovate-triage-status-poll");
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ChangelogPresent, TriageVerdict.Approve), result);
        var steps = await StepsAsync(rig);
        var waitedAt = steps.First(s => s is { Name: "assess", StopReason: "Pending" }).StartedAt;
        var judgedAt = steps.Last(s => s.Name == "assess").StartedAt;
        Assert.True(judgedAt - waitedAt < RenovateTriageWorkflow.CiTimeout, $"Judged after {judgedAt - waitedAt}.");
    }

    [Fact]
    public async Task ACommitStatusThatStaysPending_IsPolledEveryFiveMinutes_UntilTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(CommitStatus("pending", null));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.CiTimeout, result.Reason);
        var pending = (await StepsAsync(rig)).Where(s => s is { Name: "assess", StopReason: "Pending" }).ToList();
        Assert.Equal((int)(RenovateTriageWorkflow.CiTimeout / RenovateTriageWorkflow.StatusPollInterval) + 1, pending.Count);
        Assert.True(pending[^1].StartedAt - pending[0].StartedAt >= RenovateTriageWorkflow.CiTimeout);
    }

    [Fact]
    public async Task ActionsStillRunning_NextToACompletedStatus_AreReassessedEveryFiveMinutes_UntilTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "queued", null, "github-actions"));
        rig.Github.Checks.Add(CommitStatus("completed", "success"));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.CiTimeout, result.Reason);
        var pending = (await StepsAsync(rig)).Where(s => s is { Name: "assess", StopReason: "Pending" }).ToList();
        Assert.Equal((int)(RenovateTriageWorkflow.CiTimeout / RenovateTriageWorkflow.StatusPollInterval) + 1, pending.Count);
        Assert.True(pending[^1].StartedAt - pending[0].StartedAt >= RenovateTriageWorkflow.CiTimeout);
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
        Assert.Equal([(ShaA, "**Auto-approved:** patch bump, Jev scored the changelog 0.78.")], rig.Github.Approvals);
        Assert.Equal([TriageRules.AutoApprovedLabel], rig.Github.AddedLabels);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageMode.Live, verdict.Mode);
        Assert.NotNull(verdict.AppliedAt);
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(Ct);
        Assert.Equal(["assess", "precheck", "migration", "assess", "jev", "verdict", "apply"], run.Steps.OrderBy(s => s.Seq).Select(s => s.Name));
    }

    [Fact]
    public async Task LiveMode_FlagsAHeadTheRulesWillNotClimb_WithTheNeedsReviewLabelAndAStickyComment()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        ChangeMoreThanVersions(rig);

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Empty(rig.Github.Approvals);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        var comment = Assert.Single(rig.Github.Comments);
        Assert.StartsWith(TriageRules.StickyMarker, comment.Body, StringComparison.Ordinal);
        Assert.Contains(TriageRules.PrecheckDiffComment, comment.Body, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
    }

    [Fact]
    public async Task LiveMode_WhenTheHeadMovedBeforeTheWrite_WritesNothingAndLeavesTheVerdictUnapplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        ChangeMoreThanVersions(rig);
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == DiffApplyRead)
            {
                rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB) with { AuthorId = 5 };
            }
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(RenovateTriageOutcome.Skipped, result.Outcome);
        Assert.Empty(rig.Github.Writes);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((ShaA, null), (verdict.HeadSha, verdict.AppliedAt));
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
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == ClassifyRead)
            {
                options.Mode = TriageMode.Live;
            }
        };

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
        ChangeMoreThanVersions(rig);
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == DiffApplyRead - 1)
            {
                options.Mode = TriageMode.Shadow;
            }
        };

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

    // A head the rules never hand to the agent: the diff changes more than versions.
    private static void ChangeMoreThanVersions(Rig rig) =>
        rig.Github.Files[0] = new GitHubPullRequestFile("values.yaml", "@@ -1 +1 @@\n-port: 8080\n+port: 9090");

    private static void FailCi(Rig rig, int attempt = 1)
    {
        rig.Github.Checks.Add(new GitHubCheckRun(2, "build", "completed", "failure", "github-actions"));
        rig.Github.Jobs[2] = new GitHubJob(2, 900, attempt, "build", "failure");
        rig.Github.JobLogs[2] = "npm ERR! network timeout";
    }

    private static GitHubCheckRun CommitStatus(string status, string? conclusion) =>
        new(GitHubApiClient.CommitStatusId, "Terraform Cloud/anthony-spruyt/widgets", status, conclusion, null);

    private static void PassCi(Rig rig)
    {
        rig.Github.Checks[1] = new GitHubCheckRun(2, "build", "completed", "success", "github-actions");
    }

    private static async Task WaitForStepAsync(Rig rig, string name, string? stopReason = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
            if (await db.RunSteps.AnyAsync(s => s.Name == name && (stopReason == null || s.StopReason == stopReason), Ct))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for the {name} step.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<List<RunStep>> StepsAsync(Rig rig)
    {
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var runs = await db.Runs.Include(r => r.Steps).ToListAsync(Ct);
        return [.. runs.OrderBy(r => r.StartedAt).Last().Steps.OrderBy(s => s.Seq)];
    }

    [Fact]
    public async Task LiveMode_ReportsTheAgentTriageCheck_InProgressThenSuccess()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-step2-live-approve"));

        Assert.All(rig.CheckRuns.Reports, r => Assert.Equal(("Agent triage", ShaA), (r.Name, r.HeadSha)));
        Assert.All(rig.CheckRuns.Reports, r => Assert.Equal($"renovate-triage/{Repo}/7@{ShaA}", r.ExternalId));
        Assert.Contains(rig.CheckRuns.Reports, r => r.Status == CheckRunStatus.InProgress);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Success), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AJudgedCommit_StartsAFreshCheckOnce_AndOnlyTheFirstOpenReportAsksForIt()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.CheckRuns.Reports.Count > 1);
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle, "renovate-triage-fresh-check-pending-then-approve");
        });

        var open = rig.CheckRuns.Reports.Where(r => r.Status != CheckRunStatus.Completed).ToList();
        Assert.True(open.Count > 1);
        Assert.True(open[0].StartNew);
        Assert.All(open.Skip(1), r => Assert.False(r.StartNew));
        Assert.All(rig.CheckRuns.Reports.Where(r => r.Status == CheckRunStatus.Completed), r => Assert.False(r.StartNew));
    }

    [Fact]
    public async Task LiveMode_ACommitDecidedWithoutWaiting_StillOpensAFreshCheckBeforeItsResult()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.PullRequest = FakeGitHubApi.Renovate("renovate/cilium-1.x");

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-fresh-check-decided"));

        var first = rig.CheckRuns.Reports[0];
        Assert.Equal((CheckRunStatus.InProgress, true), (first.Status, first.StartNew));
        Assert.Equal(CheckRunStatus.Completed, rig.CheckRuns.Reports[^1].Status);
    }

    [Fact]
    public async Task LiveMode_ASkippedPr_NeverAsksForAFreshCheck()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = 5 };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.All(rig.CheckRuns.Reports, r => Assert.False(r.StartNew));
    }

    [Fact]
    public async Task LiveMode_ACommitJudgedBefore_NeverAsksForAFreshCheck()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        var decision = new TriageDecision(TriageVerdict.NeedsReview, TriageRules.CiFailed, null);
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, decision, TriageMode.Live, null, Ct);
        await rig.Data.Verdicts.MarkAppliedAsync(Repo, 7, ShaA, decision.Verdict, VerdictAction.Comment, Ct);

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.All(rig.CheckRuns.Reports, r => Assert.False(r.StartNew));
    }

    [Fact]
    public async Task ShadowMode_NeverReportsACheck()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Empty(rig.CheckRuns.Reports);
    }

    [Fact]
    public async Task LiveMode_ASkippedPr_ClosesTheCheckAsSkipped()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.PullRequest = rig.Github.PullRequest with { AuthorId = 5 };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Skipped), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AWriteThatFailsForGood_ClosesTheCheckAsFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.WriteFailure = new GitHubApiException("unprocessable", System.Net.HttpStatusCode.UnprocessableEntity);

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await Assert.ThrowsAsync<WorkflowFailedException>(() => RunToResultAsync(handle));
            return 0;
        });

        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Failure), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AVerdictThatIsRefusedBecauseTheHeadMoved_ClosesTheCheckAsNeutral()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == ApplyRead)
            {
                rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            }
        };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        var last = rig.CheckRuns.Reports.Last(r => r.HeadSha == ShaA);
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_ANewHeadCommit_ClosesTheOldCheckAsSuperseded()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.CheckRuns.Reports.Count > 0);
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            rig.Github.Checks.Clear();
            rig.Github.Checks.Add(new GitHubCheckRun(3, "test", "completed", "success", "github-actions"));
            await SignalAsync(env, Signal(ShaB, suite: 2));
            var first = Handle(env, handle.Id, handle.ResultRunId);
            var latest = await RunToResultAsync(handle, "renovate-triage-step2-new-head");
            await HistoryStore.RecordAsync(first, "renovate-triage-step2-new-head-superseded");
            return latest;
        });

        var old = rig.CheckRuns.Reports.Where(r => r.HeadSha == ShaA).ToList();
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (old[^1].Status, old[^1].Conclusion));
        Assert.Contains("Superseded", old[^1].Summary, StringComparison.Ordinal);
        Assert.Equal(CheckRunConclusion.Success, rig.CheckRuns.Reports[^1].Conclusion);
    }

    [Fact]
    public async Task LiveMode_WaitingForCi_IsReportedOnce_NotOnEverySignal()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.Github.Calls.Count(c => c.StartsWith("checks", StringComparison.Ordinal)) == 1);
            await SignalAsync(env, Signal(suite: 2));
            await WaitForAsync(() => rig.Github.Calls.Count(c => c.StartsWith("checks", StringComparison.Ordinal)) == 2);
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            await SignalAsync(env, Signal(suite: 3));
            return await RunToResultAsync(handle);
        });

        Assert.Single(rig.CheckRuns.Reports, r => r.Summary == "Waiting for CI.");
    }

    [Fact]
    public async Task AFailingCheckReporter_DoesNotStopTheTriage()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.CheckRuns.Failure = new InvalidOperationException("the check API is down");

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.NotNull(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
    }

    [Fact]
    public async Task LiveMode_ADiffBeyondVersionUpdates_IsFlaggedForAHuman_AndJevIsNeverAsked()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        ChangeMoreThanVersions(rig);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.PrecheckDiff, TriageVerdict.Owner), result);
        Assert.Empty(rig.Jev.Bodies);
        Assert.Empty(rig.Github.Approvals);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        var steps = await StepsAsync(rig);
        Assert.Equal("diff_not_version_only", steps.Single(s => s.Name == "precheck").StopReason);
        Assert.DoesNotContain(steps, s => s.Name == "jev");
    }

    [Theory]
    [InlineData(ScanOutcome.Flagged, "body_flagged")]
    [InlineData(ScanOutcome.Unchecked, "body_unchecked")]
    public async Task ABodyTheScannerHoldsBack_IsFlaggedForAHuman_AndJevIsNeverAsked(ScanOutcome outcome, string stopReason)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Scanner.Return("This PR contains", outcome);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.Owner, result.Verdict);
        Assert.Empty(rig.Jev.Bodies);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        Assert.Equal(stopReason, (await StepsAsync(rig)).Single(s => s.Name == "precheck").StopReason);
    }

    [Fact]
    public async Task AJevAnswer_IsGivenTheScannedBody_NeverALiveRead()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        const string editedBody = "The body after the scan: an edit that was never scanned.";
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Scanner.Scanned.Count > 0)
            {
                rig.Github.PullRequest = FakeGitHubApi.Renovate(body: editedBody);
            }
        };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Contains(FakeGitHubApi.Body, rig.Scanner.Scanned);
        var jevBody = Assert.Single(rig.Jev.Bodies);
        Assert.Contains(FakeGitHubApi.Body, jevBody, StringComparison.Ordinal);
        Assert.DoesNotContain(editedBody, jevBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlakyFailure_IsRerunOnce_AndAJobOnItsSecondAttemptIsHandedToTheAgent()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            rig.Github.Jobs[2] = new GitHubJob(2, 900, 2, "build", "failure");
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Owner, result.Verdict);
        Assert.Equal(TriageRules.CiFailed, (await PendingAsync(rig)).Reason);
        Assert.Equal([900L], rig.Github.Reruns);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s is { Name: "ci-rerun", StopReason: "rerun" });
        Assert.Contains(steps, s => s is { Name: "ci-rerun", StopReason: "attempt_limit" });
    }

    [Fact]
    public async Task AfterARerun_WithNoSignal_TheTriageReassessesOnTheNextPoll_NotWhenTheTwoHoursEnd()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            rig.Github.Jobs[2] = new GitHubJob(2, 900, 2, "build", "failure");
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageRules.CiFailed, (await PendingAsync(rig)).Reason);
        var steps = await StepsAsync(rig);
        var rerunAt = steps.Single(s => s is { Name: "ci-rerun", StopReason: "rerun" }).StartedAt;
        var reassessedAt = steps.Last(s => s.Name == "assess").StartedAt;
        Assert.True(reassessedAt - rerunAt < 2 * RenovateTriageWorkflow.StatusPollInterval, $"Reassessed after {reassessedAt - rerunAt}.");
    }

    [Fact]
    public async Task AfterARerun_ANewAttemptThatFinishesWithoutASignal_IsApprovedOnALaterPoll_NotTimedOut()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var reads = 0;
            rig.Github.OnCheckRunsRead = () =>
            {
                if (rig.Github.Reruns.Count == 1 && ++reads == 2)
                {
                    PassCi(rig);
                }

                return Task.CompletedTask;
            };
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            rig.Github.Checks[1] = new GitHubCheckRun(2, "build", "in_progress", null, "github-actions");
            return await RunToResultAsync(handle, "renovate-triage-ci-poll-rerun");
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([900L], rig.Github.Reruns);
        var steps = await StepsAsync(rig);
        var rerunAt = steps.Single(s => s is { Name: "ci-rerun", StopReason: "rerun" }).StartedAt;
        Assert.Contains(steps, s => s is { Name: "assess", StopReason: "Pending" } && s.StartedAt > rerunAt);
        Assert.True(steps.Last(s => s.Name == "assess").StartedAt - rerunAt < RenovateTriageWorkflow.CiTimeout);
    }

    [Fact]
    public async Task AfterARerun_AFirstAttemptThatStillShows_IsWaitedOutOnThePolls_WithoutAskingJevAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            // A stale poll reads the checks twice (assess, then classify), so the fifth read is the third poll's assess.
            var reads = 0;
            rig.Github.OnCheckRunsRead = () =>
            {
                if (rig.Github.Reruns.Count == 1 && ++reads == 5)
                {
                    PassCi(rig);
                }

                return Task.CompletedTask;
            };
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            return await RunToResultAsync(handle, "renovate-triage-ci-poll-stale");
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([900L], rig.Github.Reruns);
        Assert.Single(rig.Jev.CiLogs);
        Assert.Equal(2, (await StepsAsync(rig)).Count(s => s is { Name: "ci-classify", StopReason: "stale" }));
    }

    [Fact]
    public async Task AFlakyFailure_ThatPassesOnTheRerun_IsApproved_AfterOneRerun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            PassCi(rig);
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([900L], rig.Github.Reruns);
    }

    [Fact]
    public async Task ASignalThatArrivesWhileTheFailureIsClassified_DoesNotEndTheWaitForTheRerun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            rig.Jev.OnClassifyCi = async () =>
            {
                rig.Jev.OnClassifyCi = null;
                await SignalAsync(env, Signal(suite: 2));
            };
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            await WaitForStepAsync(rig, "ci-rerun");
            PassCi(rig);
            await SignalAsync(env, Signal(suite: 3));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([900L], rig.Github.Reruns);
        Assert.Equal(3, (await StepsAsync(rig)).Count(s => s.Name == "assess"));
    }

    [Fact]
    public async Task ASignalRightAfterTheRerun_WhileTheFirstAttemptStillShows_WaitsForTheNewAttempt()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            await SignalAsync(env, Signal(suite: 2));
            await WaitForStepAsync(rig, "ci-classify", "stale");
            PassCi(rig);
            await SignalAsync(env, Signal(suite: 3));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([900L], rig.Github.Reruns);
        Assert.Single(rig.Jev.CiLogs);
    }

    [Fact]
    public async Task ANewAttemptThatPassesBeforeTheFailureIsClassified_IsReassessedAndApproved_NotFlaggedAsFailed()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var reads = 0;
            rig.Github.OnCheckRunsRead = () =>
            {
                if (rig.Github.Reruns.Count == 1 && ++reads == 2)
                {
                    PassCi(rig);
                }

                return Task.CompletedTask;
            };
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            await SignalAsync(env, Signal(suite: 2));
            await WaitForStepAsync(rig, "ci-classify", "stale");
            await SignalAsync(env, Signal(suite: 3));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Single(rig.Jev.CiLogs);
    }

    [Fact]
    public async Task TheNewAttemptsSignalArrivingBeforeTheStaleVerdict_IsNotMissed()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var reads = 0;
            rig.Github.OnCheckRunsRead = async () =>
            {
                if (rig.Github.Reruns.Count == 1 && ++reads == 2)
                {
                    PassCi(rig);
                    await SignalAsync(env, Signal(suite: 3));
                }
            };
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        var steps = await StepsAsync(rig);
        var rerunAt = steps.Single(s => s is { Name: "ci-rerun", StopReason: "rerun" }).StartedAt;
        var lastAssessAt = steps.Last(s => s.Name == "assess").StartedAt;
        Assert.True(lastAssessAt - rerunAt < RenovateTriageWorkflow.CiTimeout, $"Reassessed after {lastAssessAt - rerunAt}.");
    }

    [Fact]
    public async Task AFirstAttemptThatStillShowsWhenTheTwoHoursEnd_IsHandedToTheAgentAsATimeout()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Github.Reruns.Count == 1);
            return await RunToResultAsync(handle);
        });

        Assert.Equal(TriageVerdict.Owner, result.Verdict);
        Assert.Equal(TriageRules.CiTimeoutDecision().Reason, (await PendingAsync(rig)).Reason);
        Assert.Equal([900L], rig.Github.Reruns);
    }

    [Fact]
    public async Task ShadowMode_AFlakyFailure_IsNeverRerun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Flaky;

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.CiFailed, result.Reason);
        Assert.Empty(rig.Github.Reruns);
        Assert.Empty(rig.Github.Writes);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s is { Name: "ci-classify", StopReason: "flaky" });
        Assert.DoesNotContain(steps, s => s.Name == "ci-rerun");
    }

    [Fact]
    public async Task ACiFailureJevCannotClassify_IsNeedsReview_WithoutARerun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        FailCi(rig);
        rig.Jev.Failure = new JevException("Jev answered with status 502.");

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.CiFailed, result.Reason);
        Assert.Empty(rig.Github.Reruns);
        Assert.Equal("jev_error", (await StepsAsync(rig)).Single(s => s.Name == "ci-classify").StopReason);
    }

    [Fact]
    public async Task ACiFailureThatIsNotFlaky_IsNeedsReview_WithoutARerun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        FailCi(rig);
        rig.Jev.CiKind = CiFailureKind.Real;

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageRules.CiFailed, result.Reason);
        Assert.Empty(rig.Github.Reruns);
    }

    [Fact]
    public async Task WhenJevFindsNoChangelog_TheFetchedSectionsAreGivenToJevAndItIsAskedAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.1);
        rig.Github.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", null, "A release without a list of changes.");
        rig.Github.PullRequest = FakeGitHubApi.Renovate(body: "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://redirect.github.com/upstream/dep) | `v1.2.2` -> `v1.2.3` |\n");

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-changelog-retry"));

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        Assert.Equal(2, rig.Jev.Bodies.Count);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s.Name == "changelog");
        Assert.Contains(steps, s => s.Name == "jev-changelog");
    }

    [Fact]
    public async Task WhenAFetchedChangelogSectionListsTheChanges_TheAskAgainIsDecidedByTheNotesFoundInCode()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.1);
        rig.Github.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", null, "- fixed the crash");
        rig.Github.PullRequest = FakeGitHubApi.Renovate(body: "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://redirect.github.com/upstream/dep) | `v1.2.2` -> `v1.2.3` |\n");

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-changelog-notes"));

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal(TriageRules.ChangelogPresent, result.Reason);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s.Name == "jev-changelog");
        Assert.Equal("**Auto-approved:** patch bump with release notes from [upstream/dep v1.2.3](https://github.com/upstream/dep/releases/tag/v1.2.3).", Assert.Single(await VerdictsAsync(rig)).Comment);
    }

    [Fact]
    public async Task TheTriageContextIsServedByTheGetTriageContextQuery_OnceAVerdictClimbs()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);

        var context = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await RunToResultAsync(handle, "renovate-triage-step2-context");
            return await handle.QueryAsync(wf => wf.GetTriageContext());
        });

        Assert.NotNull(context);
        var all = string.Join('\n', context.Sections);
        Assert.Contains($"Repository: {Repo}", all, StringComparison.Ordinal);
        Assert.Contains("Triage reason: ci-failed", all, StringComparison.Ordinal);
        Assert.Contains("npm ERR! network timeout", all, StringComparison.Ordinal);
        Assert.Contains(await StepsAsync(rig), s => s is { Name: "context", StopReason: "built" });
    }

    [Fact]
    public async Task AnApprove_BuildsNoTriageContext()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };

        var context = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await RunToResultAsync(handle);
            return await handle.QueryAsync(wf => wf.GetTriageContext());
        });

        Assert.Null(context);
    }

    private const string TwoDependencyBody =
        "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n"
        + "| [dep](https://redirect.github.com/upstream/dep) | `v1.2.2` -> `v1.2.3` |\n"
        + "| [other](https://redirect.github.com/upstream/other) | `v2.0.0` -> `v2.0.1` |\n";

    [Theory]
    [InlineData(ScanOutcome.Flagged)]
    [InlineData(ScanOutcome.Unchecked)]
    public async Task WhenAChangelogSectionIsHeldBack_JevIsNotAskedAgain_AndThePrClimbs(ScanOutcome held)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Answers = new JevAnswers(0.04, "patch", 0.1);
        rig.Github.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", null, "- fixed the crash");
        rig.Github.Releases["upstream/other:v2.0.1"] = new GitHubRelease("v2.0.1", null, "- held notes");
        rig.Github.PullRequest = FakeGitHubApi.Renovate(body: TwoDependencyBody);
        rig.Scanner.Return("held notes", held);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.NoChangelog, TriageVerdict.NeedsReview), result);
        Assert.Single(rig.Jev.Bodies);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s.Name == "changelog");
        Assert.DoesNotContain(steps, s => s.Name == "jev-changelog");
    }

    private static async Task SeedUnappliedApproveAsync(Rig rig) =>
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, "**Auto-approved:** patch bump with changelog."), TriageMode.Live, null, Ct);

    [Fact]
    public async Task AnUnappliedApprove_ThatPassesTheApplyTimePrecheck_IsApplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        await SeedUnappliedApproveAsync(rig);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-step2-apply-precheck-passed"));

        Assert.Equal(TriageVerdict.Approve, result.Verdict);
        Assert.Equal([ShaA], rig.Github.Approvals.Select(a => a.CommitId));
        Assert.NotNull(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
        var steps = await StepsAsync(rig);
        Assert.Equal("passed", steps.Single(s => s.Name == "apply-precheck").StopReason);
        Assert.Equal(["assess", "apply-precheck", "verdict", "apply"], steps.Select(s => s.Name));
    }

    [Fact]
    public async Task AnUnappliedApprove_ThatFailsTheApplyTimePrecheck_IsFlagged_AndStaysUnapplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        await SeedUnappliedApproveAsync(rig);
        rig.Github.Files[0] = new GitHubPullRequestFile("values.yaml", "@@ -1 +1 @@\n-port: 8080\n+port: 9090");

        var (first, second) = await InWorkerAsync(env, rig, async () =>
        {
            var one = await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-step2-apply-precheck-failed");
            var two = await RunToResultAsync(await StartAsync(env, Signal(suite: 2)));
            return (one, two);
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.PrecheckDiff, TriageVerdict.NeedsReview), first);
        Assert.Empty(rig.Github.Approvals);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        var row = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((TriageVerdict.Approve, null), (row.Verdict, row.AppliedAt));
        Assert.Equal(RenovateTriageOutcome.Skipped, second.Outcome);
        Assert.Equal(TriageRules.BlockingLabel, second.Reason);
    }

    [Fact]
    public async Task LiveMode_AMigrationFound_IsNeedsReviewWithoutAskingJev()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.PullRequest = FakeGitHubApi.Renovate(body: FakeGitHubApi.Body.Replace("[dep]", "[kubelet]", StringComparison.Ordinal));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, MigrationDetector.Reason, TriageVerdict.Owner), result);
        Assert.Empty(rig.Jev.Bodies);
        Assert.Equal("found", (await StepsAsync(rig)).Single(s => s.Name == "migration").StopReason);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
    }

    [Fact]
    public async Task LiveMode_ACancelledTriage_ClosesTheCheckAsNeutral()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.CheckRuns.Reports.Any(r => r.Summary == "Waiting for CI."));
            await handle.CancelAsync(new WorkflowCancelOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
            await Assert.ThrowsAsync<WorkflowFailedException>(() => RunToResultAsync(handle));
            return 0;
        });

        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
        Assert.Contains("cancelled", last.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveMode_ACommitThatWasJudgedBefore_ClosesTheCheckAsSkipped()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        var decision = new TriageDecision(TriageVerdict.NeedsReview, TriageRules.CiFailed, null);
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, decision, TriageMode.Live, null, Ct);
        await rig.Data.Verdicts.MarkAppliedAsync(Repo, 7, ShaA, decision.Verdict, VerdictAction.Comment, Ct);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(RenovateTriageOutcome.AlreadyJudged, result.Outcome);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Skipped), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AVerdictThatApplyRefuses_ClosesTheCheckAsNeutral()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == ApplyRead)
            {
                rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.NeedsReviewLabel] };
            }
        };

        await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Empty(rig.Github.Approvals);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
        Assert.Contains("was not applied", last.Summary, StringComparison.Ordinal);
        Assert.Contains(await StepsAsync(rig), s => s is { Name: "apply", StopReason: "Refused" });
    }

    [Fact]
    public async Task LiveMode_AModeSwitchedToShadowWhileWaiting_DoesNotLeaveTheCheckFailed()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        var options = new RenovateTriageOptions { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        var result = await InWorkerAsync(env, rig, options, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.CheckRuns.Reports.Count > 0);
            options.Mode = TriageMode.Shadow;
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(RenovateTriageOutcome.Judged, result.Outcome);
        Assert.DoesNotContain(rig.CheckRuns.Reports, r => r.Conclusion == CheckRunConclusion.Failure);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
    }
}
