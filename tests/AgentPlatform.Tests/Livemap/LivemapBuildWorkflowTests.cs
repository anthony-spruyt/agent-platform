using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildWorkflowTests
{
    private const long ExeCapture = FakeLivemapGitHub.ExeCaptureRunId;
    private const long Compat = FakeLivemapGitHub.CompatRunId;

    internal static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, LivemapBuildRig rig, Func<Task<T>> body, TimeProvider? time = null)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapBuildWorkflow>()
                .AddAllActivities(rig.Activities)
                .AddAllActivities(rig.PullRequestActivities(time)));
        return await worker.ExecuteAsync(body, Ct);
    }

    internal static Task<WorkflowHandle<LivemapBuildWorkflow, LivemapBuildOutcome>> StartAsync(WorkflowEnvironment env) =>
        env.Client.StartWorkflowAsync(
            (LivemapBuildWorkflow wf) => wf.RunAsync(new LivemapBuildInput(BuildId, Gid)),
            new WorkflowOptions($"{LivemapBuildWorkflow.WorkflowId(BuildId)}-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

    internal static Task<LivemapBuildOutcome> ResultAsync(WorkflowHandle<LivemapBuildWorkflow, LivemapBuildOutcome> handle) =>
        handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct });

    internal static async Task<LivemapBuildOutcome> RunToTheEndAsync(WorkflowEnvironment env, LivemapBuildRig rig) =>
        await InWorkerAsync(env, rig, async () => await ResultAsync(await StartAsync(env)));

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        giveUp.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(20, giveUp.Token);
        }
    }

    private static async Task<LivemapBuildRig> SeededAsync()
    {
        var rig = new LivemapBuildRig();
        rig.Config.AutoMerge = false;
        await rig.SeedAsync();
        return rig;
    }

    internal static void Succeed(LivemapBuildRig rig, CompatArtifact artifact)
    {
        rig.GitHub.ScriptRun(ExeCapture, Finished(ExeCapture));
        rig.GitHub.ScriptRun(Compat, Finished(Compat));
        rig.GitHub.Artifacts[Compat] = artifact;
    }

    [Fact]
    public async Task ABuildInFlight_IsCapturedThenChecked_ThenItsPullRequestIsOpened()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());

        var outcome = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            var result = await ResultAsync(handle);
            await HistoryStore.RecordAsync(handle, "livemap-build-pr-waiting");
            return result;
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Equal(
            [(LivemapWorkflowFile.ExeCapture, BuildId, Gid), (LivemapWorkflowFile.Compat, BuildId, Gid)],
            rig.GitHub.Dispatches);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.PullRequestWaiting, row.State);
        Assert.Equal(LivemapClassification.Known, row.Classification);
    }

    [Fact]
    public async Task ABuildWhoseResultNeedsTheOwner_StopsThere()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, HardArtifact());

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ARunThatIsStillGoing_IsReadAgainLater()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture), Running(ExeCapture), Finished(ExeCapture));

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.Equal(3, rig.GitHub.ReadsOf(ExeCapture));
    }

    internal static async Task<TimeSpan> ElapsedAsync(WorkflowHandle<LivemapBuildWorkflow, LivemapBuildOutcome> handle)
    {
        var events = (await handle.FetchHistoryAsync()).Events.ToList();
        return events[^1].EventTime.ToDateTimeOffset() - events[0].EventTime.ToDateTimeOffset();
    }

    [Fact]
    public async Task AWakeFromTheRunsOwnCheckSuite_MakesTheWorkflowReadTheRunAtOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture), Finished(ExeCapture));

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 1);
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(Sha, ExeCaptureSuiteId)));
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 2);
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.True(elapsed < LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Theory]
    [InlineData(CompatSuiteId, Sha)]
    [InlineData(1234, Sha)]
    [InlineData(1234, "2222222222222222222222222222222222222222")]
    public async Task AWakeFromAnotherCheckSuite_LeavesTheWaitToTheInterval(long suiteId, string sha)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture), Finished(ExeCapture));

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 1);
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(sha, suiteId)));
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.True(elapsed >= LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Fact]
    public async Task AWakeThatArrivesWhileTheRunIsBeingRead_StillMakesTheWorkflowReadItAgainAtOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture), Finished(ExeCapture));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.GitHub.ReadGate = release.Task;

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 1);
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(Sha, ExeCaptureSuiteId)));
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            release.SetResult();
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 2);
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.True(elapsed < LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Fact]
    public async Task ARunWithoutACheckSuite_IsNotWokenByAWakeWithoutOne()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture) with { CheckSuiteId = 0 }, Finished(ExeCapture));

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 1);
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(Sha, 0)));
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.True(elapsed >= LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Fact]
    public async Task AWakeOfTheCaptureRun_DoesNotCutTheCheckRunsWaitShort()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(Compat, Running(Compat), Finished(Compat));

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(Compat) >= 1);
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(Sha, ExeCaptureSuiteId)));
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.PullRequestWaiting, outcome);
        Assert.True(elapsed >= LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Fact]
    public async Task ARunThatNeverFinishes_ParksTheBuild_AfterTheRunLimit_WithoutGoingOn()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture));

        var (outcome, waited) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.InRange(waited, LivemapBuildWorkflow.RunLimit, LivemapBuildWorkflow.RunLimit + TimeSpan.FromMinutes(1));
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("in time", row.Detail, StringComparison.Ordinal);
        Assert.Single(rig.GitHub.Dispatches);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task AFailedCapture_ParksTheBuild_WithoutCheckingIt(string conclusion)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        rig.GitHub.ScriptRun(ExeCapture, Finished(ExeCapture, conclusion));

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains(conclusion, row.Detail, StringComparison.Ordinal);
        Assert.Equal(LivemapWorkflowFile.ExeCapture, Assert.Single(rig.GitHub.Dispatches).Workflow);
    }

    [Fact]
    public async Task ACaptureRunGitHubCannotFind_ParksTheBuild()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Single(rig.GitHub.Dispatches);
        Assert.Contains("found", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCheck_ParksTheBuild()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(Compat, Finished(Compat, "failure"));

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("failure", row.Detail, StringComparison.Ordinal);
        Assert.Equal(2, rig.GitHub.Dispatches.Count);
    }

    [Fact]
    public async Task ADispatchGitHubRefuses_ParksTheBuild_AndIsNotRetried()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        rig.GitHub.DispatchFailure = new InvalidOperationException("refused");

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Single(rig.GitHub.Dispatches);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("dispatch", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultThatCannotBeRead_ParksTheBuild_AfterTheRetries()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ForkBuildsFailure = new InvalidOperationException("down");

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Equal(LivemapBuildState.Parked, (await rig.RowAsync()).State);
    }

    [Theory]
    [InlineData(LivemapBuildState.Superseded, LivemapBuildOutcome.Superseded)]
    [InlineData(LivemapBuildState.Parked, LivemapBuildOutcome.Parked)]
    [InlineData(LivemapBuildState.NeedsOwner, LivemapBuildOutcome.NeedsOwner)]
    [InlineData(LivemapBuildState.PullRequestWaiting, LivemapBuildOutcome.PullRequestWaiting)]
    [InlineData(LivemapBuildState.Merged, LivemapBuildOutcome.Merged)]
    public async Task ABuildThatHasNothingLeftToDo_IsLeftAlone_WithoutSpendingAnything(LivemapBuildState state, LivemapBuildOutcome expected)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(state);

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(expected, outcome);
        Assert.Empty(rig.GitHub.Dispatches);
        Assert.Empty(rig.PullRequests.Calls);
        Assert.Equal(state, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ABuildThatANewerOneReplacesDuringTheCapture_IsNotChecked()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await SeededAsync();
        Succeed(rig, KnownArtifact());
        rig.GitHub.ScriptRun(ExeCapture, Running(ExeCapture), Finished(ExeCapture));

        var outcome = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => rig.GitHub.ReadsOf(ExeCapture) >= 1);
            await rig.Store.SupersedeOlderAsync(BuildId + 1, Ct);
            return await ResultAsync(handle);
        });

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Equal(LivemapWorkflowFile.ExeCapture, Assert.Single(rig.GitHub.Dispatches).Workflow);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }
}
