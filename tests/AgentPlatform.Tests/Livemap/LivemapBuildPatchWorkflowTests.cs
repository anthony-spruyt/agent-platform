using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapBuildWorkflowTests;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildPatchWorkflowTests
{
    private static readonly DateTimeOffset ResetsAt = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly object _data = new { status = "data", builds_json = EasyArtifact().BuildsJson };

    private static readonly object _code = new
    {
        status = "code",
        builds_json = EasyArtifact().BuildsJson,
        files = new[] { new { path = "src/movement.cpp", content = "// hook\n" } },
    };

    private static readonly object _gaveUp = new { status = "gave_up" };

    private static object Partial(string notes) => new { status = "partial", notes };

    private sealed class World
    {
        public LivemapBuildRig Rig { get; } = new();

        public ScriptedBudget Budget { get; } = new();

        public ScriptedPods Pods { get; } = new();

        public RecordingCleanup Cleanup { get; } = new();

        public World()
        {
            Rig.Config.HardPath = true;
            Rig.Config.AutoMerge = false;
        }

        // One answer per run, in order: null is a pod that ends without a result.
        public void AnswerWith(WorkflowEnvironment env, string summary, params object?[] answers) =>
            Pods.OnRun = async (input, info) =>
            {
                var answer = answers[Pods.Inputs.Count - 1];
                if (answer is null)
                {
                    return ScriptedPods.Failed(AgentRunOutcome.TimedOut);
                }

                await SubmitAsync(env, info.WorkflowId!, Payload(answer, summary, input));
                return ScriptedPods.Succeeded();
            };

        public void Answer(WorkflowEnvironment env, params object?[] answers) => AnswerWith(env, "The agent's summary.", answers);
    }

    private static SubmitResultPayload Payload(object data, string summary, RunAgentPodInput input) =>
        new("success", summary, JsonSerializer.SerializeToElement(data), input.Step, input.Attempt);

    private static Task SubmitAsync(WorkflowEnvironment env, string workflowId, SubmitResultPayload payload) =>
        env.Client.GetWorkflowHandle<LivemapBuildWorkflow>(workflowId).ExecuteUpdateAsync(wf => wf.SubmitResultAsync(payload));

    private static async Task<T> InPatchWorkerAsync<T>(WorkflowEnvironment env, World world, Func<Task<T>> body, bool podsRun = true)
    {
        var rig = world.Rig;
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapBuildWorkflow>()
                .AddAllActivities(rig.Activities)
                .AddAllActivities(rig.PullRequestActivities())
                .AddAllActivities(rig.HeldShipActivities())
                .AddAllActivities(rig.PatchActivities())
                .AddAllActivities(new PromptActivities(new AgentPromptsOptions(), NullLogger<PromptActivities>.Instance))
                .AddAllActivities(world.Budget)
                .AddAllActivities(world.Cleanup));
        if (!podsRun)
        {
            return await worker.ExecuteAsync(body, Ct);
        }

        using var pods = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(world.Pods));
        return await worker.ExecuteAsync(() => pods.ExecuteAsync(body, Ct), Ct);
    }

    private static Task<LivemapBuildOutcome> RunAsync(WorkflowEnvironment env, World world, string? record = null) =>
        InPatchWorkerAsync(env, world, async () =>
        {
            var handle = await StartAsync(env);
            var outcome = await ResultAsync(handle);
            if (record is not null)
            {
                await HistoryStore.RecordAsync(handle, record);
            }

            return outcome;
        });

    private static async Task<World> PatchingAsync()
    {
        var world = new World();
        await world.Rig.SeedPatchingAsync();
        return world;
    }

    [Fact]
    public async Task AHardBuild_WithTheHardPathOn_IsPatchedByTheAgent_AndItsDataPullRequestIsOpened()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = new World();
        await world.Rig.SeedAsync();
        Succeed(world.Rig, HardArtifact());
        world.Answer(env, _data);

        var outcome = await RunAsync(env, world, "livemap-build-patch-data");

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.PullRequestWaiting, LivemapClassification.Hard, 1), (row.State, row.Classification, row.AgentRuns));
        var pod = Assert.Single(world.Pods.Inputs);
        Assert.Equal(("claude-1", 1, AgentTier.Read), (pod.Step, pod.Attempt, pod.Tier));
        Assert.Equal(world.Rig.Config.AgentDeadlineSeconds, pod.DeadlineSeconds);
        Assert.Equal(new AgentRepoContext(FakeLivemapPullRequests.Fork, null, FakeLivemapPullRequests.StartMain), pod.Repo);
        Assert.Contains(FakeLivemapPullRequests.StartMain, pod.Prompt, StringComparison.Ordinal);
        Assert.Equal(world.Rig.Config.AgentModel, pod.Overrides!.Model);
        Assert.Equal("exe", Assert.Single(pod.Overrides.ImageVolumes!).Name);
        Assert.Equal(["reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
        var reserve = Assert.Single(world.Budget.Reserves);
        Assert.Equal((LivemapBudget.Kind, BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture), FakeLivemapPullRequests.Fork, FakeLivemapPullRequests.StartMain), (reserve.Kind, reserve.Subject, reserve.Scope, reserve.Revision));
        Assert.Single(world.Rig.PullRequests.Opened);
        Assert.Empty(world.Rig.PullRequests.Merges);
    }

    [Fact]
    public async Task ACodePatch_OpensAPullRequestForTheOwner_EvenWithAutoMergeOn_AndTheOwnerIsTold()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Rig.Config.AutoMerge = true;
        world.Answer(env, _code);

        var outcome = await RunAsync(env, world, "livemap-build-patch-code");

        Assert.Equal(LivemapBuildOutcome.OwnerReview, outcome);
        var row = await world.Rig.RowAsync();
        Assert.Equal(LivemapBuildState.OwnerReview, row.State);
        Assert.NotNull(row.NotifiedAt);
        Assert.Single(world.Rig.PullRequests.Opened);
        Assert.Empty(world.Rig.PullRequests.Merges);
        Assert.Equal(BuildId, Assert.Single(world.Rig.Notices.Posts).BuildId);
    }

    [Fact]
    public async Task APartialRun_SavesItsNotes_AndTheNextRunStartsFromThem()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, Partial("The table sits at 0x10."), _data);

        var outcome = await RunAsync(env, world, "livemap-build-patch-retry");

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Equal(["claude-1", "claude-2"], world.Pods.Inputs.Select(i => i.Step));
        Assert.DoesNotContain("The table sits at 0x10.", world.Pods.Inputs[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("The table sits at 0x10.", world.Pods.Inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, (await world.Rig.RowAsync()).AgentRuns);
        Assert.Equal(["reserve", "pod-created", "finish:Succeeded", "reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
    }

    [Fact]
    public async Task ARunThatEndsWithoutAResult_IsNotedForTheNextRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, null, _data);

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Contains("TimedOut", world.Pods.Inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(["reserve", "pod-created", "finish:TimedOut", "reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
        Assert.Equal(2, world.Pods.Inputs[1].Attempt);
    }

    [Fact]
    public async Task ARunWhoseAgentEndsWithoutSubmitting_IsNotedForTheNextRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, _data, _data);
        var answer = world.Pods.OnRun;
        var calls = 0;
        world.Pods.OnRun = (input, info) => ++calls > 1 ? answer(input, info) : Task.FromResult(ScriptedPods.Succeeded());

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Contains("without submitting a result", world.Pods.Inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(["reserve", "pod-created", "finish:NoResult", "reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
    }

    [Fact]
    public async Task ARunWhoseResultsWereRefused_IsNotedWithWhyForTheNextRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        var calls = 0;
        world.Answer(env, _data, _data);
        var answer = world.Pods.OnRun;
        world.Pods.OnRun = async (input, info) =>
        {
            if (++calls > 1)
            {
                return await answer(input, info);
            }

            var wrong = new SubmitResultPayload("failure", "No.", JsonSerializer.SerializeToElement(_data), input.Step, input.Attempt);
            for (var submit = 0; submit < 2; submit++)
            {
                await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SubmitAsync(env, info.WorkflowId!, wrong));
            }

            return ScriptedPods.Succeeded();
        };

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Contains("status is not", world.Pods.Inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(["reserve", "pod-created", "finish:InvalidResult", "reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
    }

    [Fact]
    public async Task AnAgentThatGivesUp_SendsTheBuildToThePerson_AfterOneRun_WithANoticeThatHoldsNoWordOfIt()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.AnswerWith(env, "The movement hook is gone from this build.", _gaveUp);

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Single(world.Pods.Inputs);
        var row = await world.Rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("movement hook is gone", row.PatchNotes, StringComparison.Ordinal);
        var post = Assert.Single(world.Rig.Notices.Posts);
        Assert.Equal(BuildId, post.BuildId);
        Assert.Contains("gave up", post.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("movement hook", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheRunCap_TheBuildNeedsAPerson_AndTheWorkflowEnds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, Partial("Run one."), Partial("Run two."), Partial("Run three."));

        var outcome = await RunAsync(env, world, "livemap-build-patch-cap");

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Equal(["claude-1", "claude-2", "claude-3"], world.Pods.Inputs.Select(i => i.Step));
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, 3), (row.State, row.AgentRuns));
        Assert.Contains("needs a person", row.Detail, StringComparison.Ordinal);
        Assert.Equal(BuildId, Assert.Single(world.Rig.Notices.Posts).BuildId);
    }

    [Fact]
    public async Task AUsageLimit_ParksTheBuildUntilTheReset_AndGivesTheRunBack()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Pods.OnRun = (_, _) => Task.FromResult(ScriptedPods.Failed(AgentRunOutcome.UsageLimit, ResetsAt));

        var outcome = await RunAsync(env, world, "livemap-build-patch-usage-limit");

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.Parked, ResetsAt, 0), (row.State, row.NextRetryAt, row.AgentRuns));
        var cooldown = Assert.Single(world.Budget.Cooldowns);
        Assert.Equal((ClaudeCooldown.Global, ResetsAt, ClaudeBudgetCauses.UsageLimit), (cooldown.Scope, cooldown.ResetsAt, cooldown.Cause));
        Assert.Equal(["reserve", "pod-created", "finish:UsageLimit", "cooldown"], world.Budget.Calls);
    }

    [Fact]
    public async Task AUsageLimit_WhoseCooldownCouldNotBeWritten_ParksTheBuildForTheUsualWait()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Pods.OnRun = (_, _) => Task.FromResult(ScriptedPods.Failed(AgentRunOutcome.UsageLimit, ResetsAt));
        world.Budget.OnCooldown = _ => throw new InvalidOperationException("The budget books are down.");

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await world.Rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.InRange(row.NextRetryAt!.Value, DateTimeOffset.UtcNow.Add(LivemapBuildActivities.RetryAfter).AddMinutes(-5), DateTimeOffset.UtcNow.Add(LivemapBuildActivities.RetryAfter).AddMinutes(5));
        Assert.Equal(0, row.AgentRuns);
    }

    [Fact]
    public async Task ABudgetThatParksTheRun_ParksTheBuildUntilItsTime_WithoutAPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Budget.OnReserve = (_, _) => ClaudeReservation.Park(ClaudeBudgetCauses.HourlyCap, ResetsAt);

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Empty(world.Pods.Inputs);
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.Parked, ResetsAt, 0), (row.State, row.NextRetryAt, row.AgentRuns));
        Assert.Contains(ClaudeBudgetCauses.HourlyCap, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABudgetThatHandsTheRunToThePerson_EndsTheBuildThere_WithoutAPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Budget.OnReserve = (_, _) => ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted);

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Empty(world.Pods.Inputs);
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, 0), (row.State, row.AgentRuns));
        Assert.Contains(ClaudeBudgetCauses.AttemptsExhausted, row.Detail, StringComparison.Ordinal);
        Assert.Equal(BuildId, Assert.Single(world.Rig.Notices.Posts).BuildId);
    }

    [Fact]
    public async Task APatchingBuild_WhenTheHardPathIsTurnedOff_EndsWithThePerson_WithoutABudgetOrAPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Rig.Config.HardPath = false;

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Empty(world.Pods.Inputs);
        Assert.Empty(world.Budget.Calls);
        var row = await world.Rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, 0), (row.State, row.AgentRuns));
        Assert.Equal(BuildId, Assert.Single(world.Rig.Notices.Posts).BuildId);
    }

    [Fact]
    public async Task ABuildThatChangesBeforeTheRunBegins_GivesTheReservationBack_AndRunsNoPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        var changed = false;
        world.Rig.PullRequests.Hook = async call =>
        {
            if (call == "main-head" && !changed)
            {
                changed = true;
                Assert.True(await world.Rig.Store.NeedsPersonAsync(BuildId, "A person took it.", Ct));
            }
        };

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Empty(world.Pods.Inputs);
        Assert.Equal(["reserve", "finish:NotStarted"], world.Budget.Calls);
        Assert.Equal(0, (await world.Rig.RowAsync()).AgentRuns);
    }

    [Fact]
    public async Task APodThatFailsForGood_ParksTheBuild_NamingTheStep_AndKeepsTheRunCounted()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        TimeSpan? scheduleToClose = null;
        world.Pods.OnRun = (_, info) =>
        {
            scheduleToClose = info.ScheduleToCloseTimeout;
            throw new ApplicationFailureException("The pod is gone.", nonRetryable: true);
        };

        var outcome = await RunAsync(env, world, "livemap-build-patch-pod-failed");

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Equal(AgentRunTimeouts.ActivityStartToClose(world.Rig.Config.AgentDeadlineSeconds) + TimeSpan.FromMinutes(60), scheduleToClose);
        var row = await world.Rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("patch run", row.Detail, StringComparison.Ordinal);
        Assert.Equal(1, row.AgentRuns);
        Assert.Equal(["reserve", "pod-created", "finish:PodFailed"], world.Budget.Calls);
    }

    [Fact]
    public async Task AStepThatKeepsFailing_ParksTheBuild_NamingTheStep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Rig.PullRequests.Hook = _ => throw new InvalidOperationException("The fork is down.");

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Empty(world.Pods.Inputs);
        Assert.Contains("patch prepare", (await world.Rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWakeDuringTheAgentRun_ChangesNothing()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, _data);
        var answer = world.Pods.OnRun;
        world.Pods.OnRun = async (input, info) =>
        {
            await env.Client.GetWorkflowHandle<LivemapBuildWorkflow>(info.WorkflowId!).SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(Sha, CompatSuiteId)));
            return await answer(input, info);
        };

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Single(world.Pods.Inputs);
    }

    [Fact]
    public async Task AResultFromAPodThatThenFails_IsNotUsed_AndTheRunIsTriedAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, _data, _data);
        var answer = world.Pods.OnRun;
        world.Pods.OnRun = async (input, info) =>
        {
            var result = await answer(input, info);
            return world.Pods.Inputs.Count == 1 ? ScriptedPods.Failed(AgentRunOutcome.TimedOut) : result;
        };

        var outcome = await RunAsync(env, world);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Equal(2, world.Pods.Inputs.Count);
        Assert.Equal(["reserve", "pod-created", "finish:TimedOut", "reserve", "pod-created", "finish:Succeeded"], world.Budget.Calls);
    }

    [Fact]
    public async Task ACancelledBuild_WhileThePodRuns_FinishesTheRunAsCancelled()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        var release = new TaskCompletionSource();
        world.Pods.OnRun = (_, _) => ScriptedPods.HeldAsync(release.Task);

        await InPatchWorkerAsync(env, world, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => world.Pods.Inputs.Count == 1);
            await handle.CancelAsync(new WorkflowCancelOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
            while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.ActivityTaskCancelRequested))
            {
                await Task.Delay(50, Ct);
            }

            release.SetResult();
            await Assert.ThrowsAsync<WorkflowFailedException>(() => ResultAsync(handle));
            return 0;
        });

        Assert.Equal(["reserve", "pod-created", "finish:Cancelled"], world.Budget.Calls);
        Assert.Equal(LivemapBuildState.Patching, (await world.Rig.RowAsync()).State);
    }

    [Fact]
    public async Task ACancelledBuild_WhileNoNodeHasPickedUpThePod_FinishesTheRunAsCancelled()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();

        await InPatchWorkerAsync(
            env,
            world,
            async () =>
            {
                var handle = await StartAsync(env);
                while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name == "RunAgentPod"))
                {
                    await Task.Delay(50, Ct);
                }

                await handle.CancelAsync(new WorkflowCancelOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
                await Assert.ThrowsAsync<WorkflowFailedException>(() => ResultAsync(handle));
                return 0;
            },
            podsRun: false);

        Assert.Equal(["reserve", "pod-created", "finish:Cancelled"], world.Budget.Calls);
        Assert.Equal(LivemapBuildState.Patching, (await world.Rig.RowAsync()).State);
    }

    [Fact]
    public async Task ThePatchStep_IsGatedBehindItsPatchMarker()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var world = await PatchingAsync();
        world.Answer(env, _gaveUp);

        var markers = await InPatchWorkerAsync(env, world, async () =>
        {
            var handle = await StartAsync(env);
            await ResultAsync(handle);
            var events = (await handle.FetchHistoryAsync()).Events;
            return events
                .Where(e => e.MarkerRecordedEventAttributes?.MarkerName == "core_patch")
                .SelectMany(e => e.MarkerRecordedEventAttributes.Details.Values.SelectMany(d => d.Payloads_))
                .Select(p => p.Data.ToStringUtf8())
                .ToList();
        });

        Assert.Contains(markers, m => m.Contains("livemap-hard-path", StringComparison.Ordinal));
    }
}
