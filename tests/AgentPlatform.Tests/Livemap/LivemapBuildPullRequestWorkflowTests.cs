using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using Temporalio.Activities;
using Temporalio.Testing;
using Temporalio.Worker;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapBuildWorkflowTests;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildPullRequestWorkflowTests
{
    private const string OtherSha = "2222222222222222222222222222222222222222";

    private sealed class PlansThatNeverEnd
    {
        private int _plans;

        private int _opens;

        public int Plans => Volatile.Read(ref _plans);

        public int Opens => Volatile.Read(ref _opens);

        [Activity(LivemapPullRequestActivities.PlanName)]
        public Task<LivemapPlan> PlanAsync(long buildId)
        {
            Interlocked.Increment(ref _plans);
            return Task.FromResult(new LivemapPlan(LivemapStep.OpenPullRequest));
        }

        [Activity(LivemapPullRequestActivities.OpenPullRequestName)]
        public Task OpenPullRequestAsync(long buildId)
        {
            Interlocked.Increment(ref _opens);
            return Task.CompletedTask;
        }
    }

    private static async Task<LivemapBuildRig> RigAsync(LivemapBuildState state = LivemapBuildState.Detected)
    {
        var rig = new LivemapBuildRig();
        rig.PullRequests.DefaultChecks = GreenChecks();
        await rig.SeedAsync(state);
        return rig;
    }

    private static List<LivemapCheckRun> Pending() =>
        [.. RequiredChecks.Select(name => CheckRun(name, "in_progress", null))];

    private static string HeadOfThePullRequest(LivemapBuildRig rig) => rig.PullRequests.CheckRunReads.First();

    [Fact]
    public async Task ABuildWhoseChecksPass_IsMerged()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());

        var outcome = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            var result = await ResultAsync(handle);
            await HistoryStore.RecordAsync(handle, "livemap-build-pr-merged");
            return result;
        });

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.Equal(2, rig.GitHub.Dispatches.Count);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.ReleaseWaiting, row.State);
        var merge = Assert.Single(rig.PullRequests.Merges);
        Assert.Equal((row.PullRequestNumber, row.HeadSha), (merge.Number, merge.Sha));
    }

    [Fact]
    public async Task AWakeAtThePullRequestsHead_MakesTheWorkflowReadItsChecksAtOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        rig.PullRequests.DefaultChecks = Pending();

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => !rig.PullRequests.CheckRunReads.IsEmpty);
            rig.PullRequests.DefaultChecks = GreenChecks();
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(HeadOfThePullRequest(rig), 9100)));
            await WaitUntilAsync(() => rig.PullRequests.CheckRunReads.Count >= 2);
            var result = await ResultAsync(handle);
            await HistoryStore.RecordAsync(handle, "livemap-build-pr-woken");
            return (result, await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.True(elapsed < LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Theory]
    [InlineData(OtherSha)]
    [InlineData(Sha)]
    public async Task AWakeAtAnotherCommit_LeavesTheChecksToTheInterval(string sha)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        rig.PullRequests.DefaultChecks = Pending();

        var (outcome, elapsed) = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => !rig.PullRequests.CheckRunReads.IsEmpty);
            rig.PullRequests.DefaultChecks = GreenChecks();
            await handle.SignalAsync(wf => wf.WakeAsync(new LivemapBuildWake(sha, 9100)));
            return (await ResultAsync(handle), await ElapsedAsync(handle));
        });

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.True(elapsed >= LivemapBuildWorkflow.CheckInterval, $"The wait took {elapsed}.");
    }

    [Fact]
    public async Task ChecksThatNeverFinish_EndTheBuildWithTheOwner_AndLeaveThePullRequestOpen()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        rig.PullRequests.DefaultChecks = Pending();

        var outcome = await InWorkerAsync(
            env,
            rig,
            async () => await ResultAsync(await StartAsync(env)),
            new TickingTimeProvider(Now, TimeSpan.FromMinutes(20)));

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.True(rig.PullRequests.CheckRunReads.Count > 1);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("in time", row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal("open", rig.PullRequests.Pulls[row.PullRequestNumber!.Value].State);
    }

    [Fact]
    public async Task AFailedRequiredCheck_EndsTheBuildWithTheOwner_NamingTheCheck()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        rig.PullRequests.DefaultChecks = [CheckRun(RequiredChecks[0], conclusion: "failure"), .. GreenChecks().Skip(1)];

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        var row = await rig.RowAsync();
        Assert.Contains(RequiredChecks[0], row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal("open", rig.PullRequests.Pulls[row.PullRequestNumber!.Value].State);
    }

    [Fact]
    public async Task WhenMainMovesUnderThePullRequest_ItIsOpenedAgainOnTheNewMain_WithoutCheckingTheBuildAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        var moved = new string('d', 40);
        var hooked = 0;
        rig.PullRequests.Hook = call =>
        {
            if (call.StartsWith("open:", StringComparison.Ordinal) && Interlocked.Increment(ref hooked) == 1)
            {
                rig.PullRequests.MainHead = moved;
            }

            return Task.CompletedTask;
        };

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.Equal(2, rig.GitHub.Dispatches.Count);
        Assert.Equal(2, rig.PullRequests.Opened.Count);
        Assert.Equal([rig.PullRequests.Opened[0].Number], rig.PullRequests.Closed);
        Assert.Equal(moved, rig.PullRequests.Commits[1].ExpectedHeadOid);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.ReleaseWaiting, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(rig.PullRequests.Opened[1].Number, Assert.Single(rig.PullRequests.Merges).Number);
    }

    [Fact]
    public async Task ABuildAnotherOneReplacesWhileItsChecksRun_IsNotMerged()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        rig.PullRequests.DefaultChecks = Pending();

        var outcome = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => !rig.PullRequests.CheckRunReads.IsEmpty);
            rig.PullRequests.DefaultChecks = GreenChecks();
            await rig.Store.SupersedeOlderAsync(BuildId + 1, Ct);
            return await ResultAsync(handle);
        });

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AClassifiedBuild_GoesStraightToItsPullRequest_WithoutRunningTheFork()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapBuildRig();
        rig.PullRequests.DefaultChecks = GreenChecks();
        await rig.SeedClassifiedAsync();

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.Empty(rig.GitHub.Dispatches);
        Assert.Single(rig.PullRequests.Merges);
    }

    [Fact]
    public async Task AClassifiedBuild_WhoseCompatArtifactIsGone_IsCheckedAgainFromTheStart()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await RigAsync();
        Succeed(rig, KnownArtifact());
        Assert.True(await rig.Store.ClassifyAsync(BuildId, LivemapClassification.Known, 7777, Ct));

        var outcome = await RunToTheEndAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.Equal(2, rig.GitHub.Dispatches.Count);
        Assert.Equal(0, (await rig.RowAsync()).Attempts);
    }

    [Fact]
    public async Task APlanThatNeverEnds_ParksTheBuild_AfterTheTurnLimit()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapBuildRig();
        await rig.SeedClassifiedAsync();
        var plans = new PlansThatNeverEnd();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapBuildWorkflow>()
                .AddAllActivities(rig.Activities)
                .AddAllActivities(plans));

        var outcome = await worker.ExecuteAsync(async () => await ResultAsync(await StartAsync(env)), Ct);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Equal(LivemapBuildWorkflow.MaxTurns, plans.Plans);
        Assert.Equal(LivemapBuildWorkflow.MaxTurns, plans.Opens);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("settle", row.Detail, StringComparison.Ordinal);
    }
}
