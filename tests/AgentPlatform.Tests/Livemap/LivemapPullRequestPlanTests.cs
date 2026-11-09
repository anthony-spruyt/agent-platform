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
        { LivemapBuildState.Merged, LivemapStep.Stop, LivemapBuildOutcome.Merged },
        { LivemapBuildState.NeedsOwner, LivemapStep.Stop, LivemapBuildOutcome.NeedsOwner },
        { LivemapBuildState.Parked, LivemapStep.Stop, LivemapBuildOutcome.Parked },
        { LivemapBuildState.Superseded, LivemapStep.Stop, LivemapBuildOutcome.Superseded },
        { LivemapBuildState.Done, LivemapStep.Stop, LivemapBuildOutcome.Superseded },
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
