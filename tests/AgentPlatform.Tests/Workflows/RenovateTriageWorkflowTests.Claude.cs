using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Workflows;

public partial class RenovateTriageWorkflowTests
{
    private static readonly DateTimeOffset ResetsAt = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] GreenReasons = ["CI is green"];

    // Checks pass and Jev reports a likely breaking change, so the rules hand the head to the agent.
    private static Rig Climbing()
    {
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Jev.Answers = new JevAnswers(0.9, "patch", 0.9);
        return rig;
    }

    private static SubmitResultPayload Submission(string verdict, int attempt, string sha = ShaA, string step = "claude", string[]? fix = null)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            verdict,
            head_sha = sha,
            versions = new[] { new { dep = "dep", @from = "1.0.0", to = "1.0.1" } },
            fix = fix ?? [],
            reasons = GreenReasons,
        });
        return new SubmitResultPayload("success", "The bump looks fine.", data, step, attempt);
    }

    private static Task SubmitAsync(WorkflowEnvironment env, string workflowId, SubmitResultPayload payload) =>
        env.Client.GetWorkflowHandle<RenovateTriageWorkflow>(workflowId).ExecuteUpdateAsync(wf => wf.SubmitResultAsync(payload));

    private static Func<RunAgentPodInput, ActivityInfo, Task<AgentRunResult>> Answers(WorkflowEnvironment env, string verdict = "SAFE", Func<Task>? during = null, string[]? fix = null) =>
        async (input, info) =>
        {
            if (during is not null)
            {
                await during();
            }

            await SubmitAsync(env, info.WorkflowId!, Submission(verdict, input.Attempt, input.Repo!.HeadSha, fix: fix));
            return ScriptedPods.Succeeded();
        };

    private static async Task ReleaseOnCancelRequestAsync(WorkflowHandle handle, TaskCompletionSource release)
    {
        while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.ActivityTaskCancelRequested))
        {
            await Task.Delay(50, Ct);
        }

        release.SetResult();
    }

    private static async Task<TriagePending> PendingAsync(Rig rig) =>
        (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!;

    [Fact]
    public async Task AClimbingHead_IsQueued_ThenAskedOfTheAgent_AndASafeAnswerIsApplied()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        (TriagePendingStatus Pending, int Verdicts, string? Note, CheckRunStatus Check)? during = null;
        rig.Pods.OnRun = Answers(env, during: async () =>
        {
            during = ((await PendingAsync(rig)).Status, (await VerdictsAsync(rig)).Count, rig.Github.Comments.Single().Body, rig.CheckRuns.Reports[^1].Status);
        });

        var (result, context) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            var judged = await RunToResultAsync(handle, "renovate-triage-claude-safe");
            return (judged, await handle.QueryAsync(wf => wf.GetTriageContext()));
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, "claude-safe", TriageVerdict.Safe), result);
        Assert.Equal(TriagePendingStatus.Pending, during!.Value.Pending);
        Assert.Equal(0, during.Value.Verdicts);
        Assert.Contains("**Queued for agent review:** breaking", during.Value.Note, StringComparison.Ordinal);
        Assert.Equal(CheckRunStatus.InProgress, during.Value.Check);
        Assert.Equal(["reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
        var pod = Assert.Single(rig.Pods.Inputs);
        Assert.Equal(("claude", 1, AgentTier.Read), (pod.Step, pod.Attempt, pod.Tier));
        Assert.Equal(new AgentRepoContext(Repo, 7, ShaA), pod.Repo);
        Assert.Contains(ShaA, pod.Prompt, StringComparison.Ordinal);
        Assert.Equal(Model, Assert.Single(rig.Budget.Reserves).Model);
        Assert.Equal(TriagePendingStatus.Settled, (await PendingAsync(rig)).Status);
        Assert.Equal([ShaA], rig.Github.Approvals.Select(a => a.CommitId));
        Assert.Contains(TriageRules.SafeLabel, rig.Github.AddedLabels);
        Assert.Contains(TriageRules.NeedsTriageLabel, rig.Github.RemovedLabels);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((TriageVerdict.Safe, "[\"claude-safe\"]", LadderRung.Claude), (verdict.Verdict, verdict.Reasons, verdict.Rung));
        Assert.NotNull(verdict.AppliedAt);
        Assert.Contains("**Agent verdict: SAFE**", Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Success), (last.Status, last.Conclusion));
        Assert.Contains("Triage reason: breaking", string.Join('\n', context!.Sections), StringComparison.Ordinal);
        var steps = await StepsAsync(rig);
        Assert.Contains(steps, s => s is { Name: "claude", Rung: LadderRung.Claude, StopReason: "submitted" });
        Assert.Contains(steps, s => s is { Name: "verdict", Rung: LadderRung.Claude });
    }

    [Theory]
    [InlineData("FIXABLE", TriageVerdict.Fixable, "claude-fixable", TriageRules.FixableLabel)]
    [InlineData("BLOCKED", TriageVerdict.Blocked, "claude-blocked", TriageRules.BlockedLabel)]
    [InlineData("NEEDS_REVIEW", TriageVerdict.Owner, "claude-needs-review", TriageRules.NeedsReviewLabel)]
    public async Task EachAgentVerdict_IsAppliedWithItsLabel_AndTheAgentsFixList(string answer, TriageVerdict verdict, string reason, string label)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = Answers(env, answer, fix: ["bump the lockfile"]);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, reason, verdict), result);
        Assert.Contains(label, rig.Github.AddedLabels);
        Assert.Contains(TriageRules.NeedsTriageLabel, rig.Github.RemovedLabels);
        Assert.Empty(rig.Github.Approvals);
        var note = Assert.Single(rig.Github.Comments).Body;
        Assert.Contains("What to fix:", note, StringComparison.Ordinal);
        Assert.Contains("bump the lockfile", note, StringComparison.Ordinal);
        Assert.Equal(verdict, Assert.Single(await VerdictsAsync(rig)).Verdict);
        var expected = verdict == TriageVerdict.Owner ? TriagePendingStatus.Owner : TriagePendingStatus.Settled;
        Assert.Equal(expected, (await PendingAsync(rig)).Status);
    }

    [Fact]
    public async Task AFullBudget_ParksTheRun_AndALaterSignalReDrivesTheSameHead()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var until = ResetsAt;
        rig.Budget.OnReserve = (_, _) => ClaudeReservation.Park(ClaudeBudgetCauses.HourlyCap, until);
        rig.Pods.OnRun = Answers(env);

        var (parked, redriven) = await InWorkerAsync(env, rig, async () =>
        {
            var first = await RunToResultAsync(await StartAsync(env, Signal(suite: 1)), "renovate-triage-claude-parked");
            Assert.Empty(rig.Pods.Inputs);
            var queued = rig.CheckRuns.Reports[^1];
            Assert.Equal((CheckRunStatus.Queued, null), (queued.Status, queued.Conclusion));
            Assert.Contains("hourly-cap", queued.Summary, StringComparison.Ordinal);
            Assert.Contains("2030-01-01 12:00 UTC", queued.Summary, StringComparison.Ordinal);
            Assert.Equal(TriagePendingStatus.Pending, (await PendingAsync(rig)).Status);
            Assert.Empty(await VerdictsAsync(rig));

            rig.Budget.OnReserve = (input, _) => ClaudeReservation.Reserved(input.RunId, 1);
            var second = await RunToResultAsync(await StartAsync(env, Signal(suite: 2)), "renovate-triage-claude-redrive");
            return (first, second);
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Parked, ShaA, ClaudeBudgetCauses.HourlyCap, null), parked);
        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, "claude-safe", TriageVerdict.Safe), redriven);
        Assert.Equal(TriagePendingStatus.Settled, (await PendingAsync(rig)).Status);
        Assert.Single(await VerdictsAsync(rig));
        Assert.Single(rig.Github.Comments);
        Assert.Contains(await StepsAsync(rig), s => s is { Name: "assess", StopReason: "Climb" });
    }

    [Fact]
    public async Task AUsageLimitStop_CountsAsAFailedAttempt_SetsTheCooldown_AndParks()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = (_, _) => Task.FromResult(ScriptedPods.Failed(AgentRunOutcome.UsageLimit, ResetsAt));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-claude-usage-limit"));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Parked, ShaA, ClaudeBudgetCauses.UsageLimit, null), result);
        Assert.Equal(["reserve", "pod-created", "finish:UsageLimit", "cooldown"], rig.Budget.Calls);
        Assert.Equal(new SetClaudeCooldownInput(ClaudeCooldown.Global, ResetsAt, ClaudeBudgetCauses.UsageLimit), Assert.Single(rig.Budget.Cooldowns));
        var queued = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Queued, null), (queued.Status, queued.Conclusion));
        Assert.Contains("usage-limit", queued.Summary, StringComparison.Ordinal);
        Assert.Empty(await VerdictsAsync(rig));
    }

    [Fact]
    public async Task AnOpenBreaker_HandsTheHeadToTheOwner_WithoutAPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Budget.OnReserve = (_, _) => ClaudeReservation.Owner(ClaudeBudgetCauses.BreakerOpen);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-claude-breaker"));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, ClaudeBudgetCauses.BreakerOpen, TriageVerdict.Owner), result);
        Assert.Empty(rig.Pods.Inputs);
        Assert.Contains(TriageRules.NeedsReviewLabel, rig.Github.AddedLabels);
        Assert.Contains("paused", Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((TriageVerdict.Owner, null), (verdict.Verdict, verdict.Rung));
        Assert.Equal(TriagePendingStatus.Owner, (await PendingAsync(rig)).Status);
    }

    [Fact]
    public async Task TwoFailedAttempts_HandTheHeadToTheOwner_AfterTheSecondFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = (_, _) => Task.FromResult(ScriptedPods.Failed(AgentRunOutcome.PodFailed));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-claude-attempts-exhausted"));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, ClaudeBudgetCauses.AttemptsExhausted, TriageVerdict.Owner), result);
        Assert.Equal([1, 2], rig.Pods.Inputs.Select(i => i.Attempt));
        Assert.Equal(
            ["reserve", "pod-created", "finish:PodFailed", "reserve", "pod-created", "finish:PodFailed", "reserve"],
            rig.Budget.Calls);
        Assert.Contains("failed twice", Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFirstAttemptThatFails_IsFollowedByASecondThatAnswers()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var answer = Answers(env);
        rig.Pods.OnRun = (input, info) => input.Attempt == 1
            ? Task.FromResult(ScriptedPods.Failed(AgentRunOutcome.TimedOut))
            : answer(input, info);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-claude-second-attempt"));

        Assert.Equal("claude-safe", result.Reason);
        Assert.Equal(["reserve", "pod-created", "finish:TimedOut", "reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
        Assert.NotEqual(rig.Budget.Reserves[0].RunId, rig.Budget.Reserves[1].RunId);
    }

    [Fact]
    public async Task AHeadThatMovesDuringThePod_CancelsIt_ClosesTheOldCheck_AndJudgesTheNewHead()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var answer = Answers(env);
        var release = new TaskCompletionSource();
        rig.Pods.OnRun = (input, info) => rig.Pods.Inputs.Count == 1 ? ScriptedPods.HeldAsync(release.Task) : answer(input, info);

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            var first = Handle(env, handle.Id, handle.ResultRunId);
            await WaitForAsync(() => rig.Pods.Inputs.Count == 1);
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            rig.Github.Commits[0] = FakeGitHubApi.RenovateCommit(ShaB);
            await SignalAsync(env, Signal(ShaB, suite: 2));
            await ReleaseOnCancelRequestAsync(first, release);
            var latest = await RunToResultAsync(handle, "renovate-triage-claude-new-head");
            await HistoryStore.RecordAsync(first, "renovate-triage-claude-new-head-superseded");
            return latest;
        });

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaB, "claude-safe", TriageVerdict.Safe), result);
        Assert.Equal([ShaA, ShaB], rig.Pods.Inputs.Select(i => i.Repo!.HeadSha));
        Assert.Equal(["reserve", "pod-created", "finish:Cancelled", "reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
        Assert.Equal(["claude"], rig.Cleanup.Cleanups.Distinct());
        var old = rig.CheckRuns.Reports.Where(r => r.HeadSha == ShaA).ToList();
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (old[^1].Status, old[^1].Conclusion));
        Assert.Contains("Superseded", old[^1].Summary, StringComparison.Ordinal);
        Assert.Equal([ShaB], rig.Github.Approvals.Select(a => a.CommitId));
    }

    [Fact]
    public async Task ASignalForTheSameHeadDuringThePod_DoesNotCancelIt()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var release = new TaskCompletionSource();
        rig.Pods.OnRun = Answers(env, during: () => release.Task);

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal(suite: 1));
            await WaitForAsync(() => rig.Pods.Inputs.Count == 1);
            var reads = rig.Github.Calls.Count(c => c == "pr");
            await SignalAsync(env, Signal(suite: 2));
            await WaitForAsync(() => rig.Github.Calls.Count(c => c == "pr") > reads);
            release.SetResult();
            return await RunToResultAsync(handle);
        });

        Assert.Equal("claude-safe", result.Reason);
        Assert.Single(rig.Pods.Inputs);
        Assert.Equal(["reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
    }

    [Fact]
    public async Task ACancelledTriage_DuringThePod_FinishesTheRunAsCancelled_AndClosesTheCheck()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var release = new TaskCompletionSource();
        rig.Pods.OnRun = (_, _) => ScriptedPods.HeldAsync(release.Task);

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.Pods.Inputs.Count == 1);
            await handle.CancelAsync(new WorkflowCancelOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
            await ReleaseOnCancelRequestAsync(handle, release);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => RunToResultAsync(handle));
            return 0;
        });

        Assert.Equal(["reserve", "pod-created", "finish:Cancelled"], rig.Budget.Calls);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
        Assert.Contains("cancelled", last.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerTheParserRejects_CanBeSentAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = async (input, info) =>
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, info.WorkflowId!, Submission("SAFE", input.Attempt, ShaB)));
            await SubmitAsync(env, info.WorkflowId!, Submission("SAFE", input.Attempt));
            return ScriptedPods.Succeeded();
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal("claude-safe", result.Reason);
        Assert.Equal(["reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
    }

    [Fact]
    public async Task TwoRejectedAnswers_FailTheAttempt_AndTheNextAttemptAnswers()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var answer = Answers(env);
        rig.Pods.OnRun = async (input, info) =>
        {
            if (input.Attempt == 2)
            {
                return await answer(input, info);
            }

            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, info.WorkflowId!, Submission("SAFE", input.Attempt, ShaB)));
            }

            return ScriptedPods.Succeeded();
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal("claude-safe", result.Reason);
        Assert.Equal(["reserve", "pod-created", "finish:InvalidResult", "reserve", "pod-created", "finish:Succeeded"], rig.Budget.Calls);
    }

    [Fact]
    public async Task APodThatSubmitsNothing_IsANoResultFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(ClaudeBudgetCauses.AttemptsExhausted, result.Reason);
        Assert.Equal(["finish:NoResult", "finish:NoResult"], rig.Budget.Calls.Where(c => c.StartsWith("finish", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnAnswerForAnotherStepOrAttempt_IsRejected()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        var answer = Answers(env);
        rig.Pods.OnRun = async (input, info) =>
        {
            var stale = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, info.WorkflowId!, Submission("SAFE", input.Attempt + 1)));
            Assert.Equal(AgentResultUpdates.StaleRunErrorType, ((ApplicationFailureException)stale.InnerException!).ErrorType);
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, info.WorkflowId!, Submission("SAFE", input.Attempt, step: "spike")));
            return await answer(input, info);
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal("claude-safe", result.Reason);
    }

    [Fact]
    public async Task AnAnswerSentWhileNoPodIsRunning_IsRejected()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));

        await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForAsync(() => rig.CheckRuns.Reports.Count > 0);
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, handle.Id, Submission("SAFE", 1)));
            rig.Github.Checks[1] = new GitHubCheckRun(2, "test", "completed", "success", "github-actions");
            await SignalAsync(env, Signal(suite: 2));
            return await RunToResultAsync(handle);
        });
    }

    [Fact]
    public async Task ABudgetStoreThatIsDownForAWhile_IsRetried()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = Answers(env);
        rig.Budget.OnReserve = (input, call) => call <= 3
            ? throw new InvalidOperationException("the database is down")
            : ClaudeReservation.Reserved(input.RunId, 1);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal("claude-safe", result.Reason);
        Assert.Equal(4, rig.Budget.Reserves.Count);
    }

    [Fact]
    public async Task ABudgetStoreThatStaysDown_ParksTheRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Budget.OnReserve = (_, _) => throw new InvalidOperationException("the database is down");

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Parked, ShaA, "budget-unavailable", null), result);
        Assert.Empty(rig.Pods.Inputs);
        Assert.Equal((CheckRunStatus.Queued, null), (rig.CheckRuns.Reports[^1].Status, rig.CheckRuns.Reports[^1].Conclusion));
    }

    [Fact]
    public async Task AContextThatCannotBeBuilt_ParksTheRun_BeforeAnyReservation()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        FailCi(rig);
        rig.Scanner.FailOn = ("network timeout", new InvalidOperationException("the scanner is down"));

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Parked, ShaA, "context-unavailable", null), result);
        Assert.Empty(rig.Budget.Calls);
        Assert.Contains(await StepsAsync(rig), s => s is { Name: "context", StopReason: "error" });
    }

    [Fact]
    public async Task AWriteThatFailsForGood_AfterTheAnswer_ClosesTheCheckAsFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = Answers(env, during: () =>
        {
            rig.Github.WriteFailure = new GitHubApiException("unprocessable", System.Net.HttpStatusCode.UnprocessableEntity);
            return Task.CompletedTask;
        });

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
    public async Task AnAnswerApplyRefuses_ClosesTheCheckAsNeutral_AndKeepsTheVerdictForARetry()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = Answers(env, "FIXABLE", during: () =>
        {
            rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [.. rig.Github.PullRequest.Labels, TriageRules.NeedsReviewLabel] };
            return Task.CompletedTask;
        });

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.Fixable, result.Verdict);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
        Assert.Contains("was not applied", last.Summary, StringComparison.Ordinal);
        Assert.Null(Assert.Single(await VerdictsAsync(rig)).AppliedAt);
        Assert.Equal(TriagePendingStatus.Pending, (await PendingAsync(rig)).Status);
    }

    [Fact]
    public async Task AHeadThatMovesBeforeTheApply_RestartsTheTriageOnTheNewHead()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = Climbing();
        rig.Pods.OnRun = Answers(env);
        var moved = false;
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Pods.Inputs.Count == 1 && !moved && rig.Budget.Calls.Contains("finish:Succeeded"))
            {
                moved = true;
                rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
                rig.Github.Commits[0] = FakeGitHubApi.RenovateCommit(ShaB);
            }
        };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            return await RunToResultAsync(handle, "renovate-triage-claude-apply-head-moved");
        });

        Assert.Equal(ShaB, result.HeadSha);
        Assert.Equal([ShaA, ShaB], rig.Pods.Inputs.Select(i => i.Repo!.HeadSha));
        var old = rig.CheckRuns.Reports.Where(r => r.HeadSha == ShaA).ToList();
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (old[^1].Status, old[^1].Conclusion));
    }

    [Fact]
    public async Task ALiveVerdictTheAgentIsNeverAsked_IsHandedToTheOwnerOnTheOwnerLabel()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Commits.Add(rig.Github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 });

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-claude-owner-handoff"));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.NonRenovateCommit, TriageVerdict.Owner), result);
        Assert.Empty(rig.Budget.Calls);
        Assert.Empty(rig.Pods.Inputs);
        Assert.Contains(TriageRules.NeedsReviewLabel, rig.Github.AddedLabels);
        Assert.Null(await rig.Data.Pending.FindAsync(Repo, 7, Ct));
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal((TriageVerdict.Owner, null), (verdict.Verdict, verdict.Rung));
    }

    [Fact]
    public async Task ShadowMode_NeverQueuesOrReservesAnything()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        rig.Jev.Answers = new JevAnswers(0.9, "patch", 0.9);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        Assert.Empty(rig.Budget.Calls);
        Assert.Empty(rig.Github.Writes);
        Assert.Null(await rig.Data.Pending.FindAsync(Repo, 7, Ct));
    }

    [Fact]
    public async Task AStoredVerdictThatWasNotApplied_IsAppliedAsItIs_WithoutAskingTheAgent()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig { Mode = TriageMode.Live };
        var decision = new TriageDecision(TriageVerdict.NeedsReview, "breaking", "**Needs review:** a breaking change is likely.");
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, decision, TriageMode.Live, null, Ct);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        Assert.Empty(rig.Budget.Calls);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
    }
}
