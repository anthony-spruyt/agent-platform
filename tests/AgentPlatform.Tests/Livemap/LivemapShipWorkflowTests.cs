using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using Microsoft.Extensions.Options;
using Temporalio.Testing;
using Temporalio.Worker;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapBuildWorkflowTests;

namespace AgentPlatform.Tests.Livemap;

public class LivemapShipWorkflowTests
{
    private static async Task<T> ShippingAsync<T>(WorkflowEnvironment env, LivemapShipRig rig, Func<Task<T>> body, TimeProvider? time = null)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapBuildWorkflow>()
                .AddAllActivities(new LivemapBuildActivities(Options.Create(rig.Config), rig.GitHub, rig.Store))
                .AddAllActivities(new LivemapPullRequestActivities(Options.Create(rig.Config), rig.Store, rig.Fork, rig.GitHub, new FakeBuildsSchema(), time ?? rig.Time))
                .AddAllActivities(rig.Activities(time)));
        return await worker.ExecuteAsync(body, Ct);
    }

    private static Task<LivemapBuildOutcome> RunAsync(WorkflowEnvironment env, LivemapShipRig rig, TimeProvider? time = null) =>
        ShippingAsync(env, rig, async () => await ResultAsync(await StartAsync(env)), time);

    [Fact]
    public async Task AMergedBuild_IsShippedThroughTheReleaseAndThePin_AndToldToTheFork()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.Hook = call =>
        {
            if (call == $"merge:{LivemapShipRig.ReleaseNumber}")
            {
                rig.PublishRelease();
            }

            return Task.CompletedTask;
        };

        var outcome = await ShippingAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            var result = await ResultAsync(handle);
            await HistoryStore.RecordAsync(handle, "livemap-build-shipped");
            return result;
        });

        Assert.Equal(LivemapBuildOutcome.Shipped, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Done, row.State);
        Assert.Equal(LivemapShipRig.ReleaseNumber, Assert.Single(rig.Fork.Merges).Number);
        var pin = Assert.Single(rig.Pin.Merges);
        Assert.Equal(Assert.Single(rig.Pin.Opened).Number, pin.Number);
        Assert.Equal(BuildId, Assert.Single(rig.Notices.Posts).BuildId);
    }

    [Fact]
    public async Task AStepThatMustWait_IsAskedAgainAfterAPause_UntilItsLimitPasses()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);

        var (outcome, elapsed) = await ShippingAsync(
            env,
            rig,
            async () =>
            {
                var handle = await StartAsync(env);
                var result = await ResultAsync(handle);
                await HistoryStore.RecordAsync(handle, "livemap-build-ship-waiting");
                return (result, await ElapsedAsync(handle));
            },
            new TickingTimeProvider(Now, TimeSpan.FromMinutes(20)));

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.True(elapsed >= LivemapBuildWorkflow.ShipInterval, $"The workflow waited {elapsed}.");
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.NotNull(row.NotifiedAt);
        Assert.Single(rig.Notices.Posts);
    }

    [Fact]
    public async Task AStepThatWaitsLongerThanTheTurnLimit_IsStillAskedUntilItsOwnLimit()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);

        var outcome = await RunAsync(env, rig, new TickingTimeProvider(Now, TimeSpan.FromMinutes(5)));

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("No release pull request", row.Detail, StringComparison.Ordinal);
        Assert.True(LivemapShipActivities.ReleaseLimit / TimeSpan.FromMinutes(5) > LivemapBuildWorkflow.MaxTurns);
    }

    [Fact]
    public async Task WithTheSwitchOff_TheReleaseIsHeld_AndTheWorkflowEnds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        rig.Config.AutoMerge = false;
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();

        var outcome = await RunAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.ReleaseWaiting, outcome);
        Assert.Empty(rig.Fork.Merges);
        Assert.Equal(LivemapBuildState.ReleaseWaiting, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WithTheSwitchOff_ThePinPullRequestIsOpenedAndHeld_AndTheWorkflowEnds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        rig.Config.AutoMerge = false;
        await rig.AtAsync(LivemapBuildState.Verified);

        var outcome = await RunAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.PinWaiting, outcome);
        Assert.Single(rig.Pin.Opened);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal(LivemapBuildState.PinWaiting, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ABuildFlaggedForTheOwner_IsToldToTheFork_BeforeTheWorkflowEnds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        rig.Pin.Contents.Clear();

        var outcome = await RunAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Equal(BuildId, Assert.Single(rig.Notices.Posts).BuildId);
        Assert.NotNull((await rig.RowAsync()).NotifiedAt);
    }

    [Fact]
    public async Task AShippedBuild_IsLeftAlone()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);
        Assert.True(await rig.Store.NotifiedAsync(BuildId, Ct));

        var outcome = await RunAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Shipped, outcome);
        Assert.Equal(0, rig.Notices.Asked);
        Assert.Empty(rig.Fork.Calls);
        Assert.Empty(rig.Pin.Calls);
    }

    [Fact]
    public async Task AStepThatKeepsFailing_ParksTheBuild_WhereItStopped()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);
        rig.Notices.Failure = new InvalidOperationException("down");

        var outcome = await RunAsync(env, rig);

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains("ship", row.Detail, StringComparison.Ordinal);
        Assert.Null(row.NotifiedAt);
    }
}
