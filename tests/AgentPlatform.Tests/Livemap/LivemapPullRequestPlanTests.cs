using AgentPlatform.Host.Livemap;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPullRequestPlanTests
{
    private static readonly string Head = new('a', 40);

    private static Task<LivemapPlan> PlanAsync(LivemapBuildRig rig) =>
        new ActivityEnvironment().RunAsync(() => rig.PullRequestActivities().PlanAsync(BuildId));

    public static TheoryData<LivemapBuildState, LivemapStep, LivemapBuildOutcome> StopsAndChecks => new()
    {
        { LivemapBuildState.Detected, LivemapStep.Check, LivemapBuildOutcome.Classified },
        { LivemapBuildState.Classified, LivemapStep.OpenPullRequest, LivemapBuildOutcome.Classified },
        { LivemapBuildState.PullRequestWaiting, LivemapStep.Stop, LivemapBuildOutcome.PullRequestWaiting },
        { LivemapBuildState.Merged, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.Released, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.Verified, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.PinOpen, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.Pinned, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.ReleaseWaiting, LivemapStep.Stop, LivemapBuildOutcome.ReleaseWaiting },
        { LivemapBuildState.PinWaiting, LivemapStep.Stop, LivemapBuildOutcome.PinWaiting },
        { LivemapBuildState.NeedsOwner, LivemapStep.Ship, LivemapBuildOutcome.Classified },
        { LivemapBuildState.Parked, LivemapStep.Stop, LivemapBuildOutcome.Parked },
        { LivemapBuildState.Superseded, LivemapStep.Stop, LivemapBuildOutcome.Superseded },
        { LivemapBuildState.Done, LivemapStep.Stop, LivemapBuildOutcome.Shipped },
    };

    [Theory]
    [MemberData(nameof(StopsAndChecks))]
    public async Task EachState_HasOneNextStep(LivemapBuildState state, LivemapStep step, LivemapBuildOutcome outcome)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(state);

        var plan = await PlanAsync(rig);

        Assert.Equal(step, plan.Step);
        Assert.Equal(outcome, plan.Outcome);
        Assert.Null(plan.HeadSha);
    }

    [Fact]
    public async Task ABuildTheOwnerWasToldAbout_IsLeftToTheOwner()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.NeedsOwner);
        Assert.True(await rig.Store.NotifiedAsync(BuildId, Ct));

        var plan = await PlanAsync(rig);

        Assert.Equal(new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.NeedsOwner, null), plan);
    }

    [Fact]
    public async Task ABuildWithNoRow_IsSuperseded()
    {
        var plan = await PlanAsync(new LivemapBuildRig());

        Assert.Equal(new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.Superseded, null), plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APullRequestBeingWatched_NamesTheHeadToWatch(bool merging)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedClassifiedAsync();
        Assert.True(await rig.Store.OpenPullRequestAsync(BuildId, 7, Head, waiting: false, Ct));
        if (merging)
        {
            Assert.True(await rig.Store.BeginMergeAsync(BuildId, Ct));
        }

        var plan = await PlanAsync(rig);

        Assert.Equal(LivemapStep.Watch, plan.Step);
        Assert.Equal(Head, plan.HeadSha);
    }
}
