using AgentPlatform.Host.Livemap;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildActivitiesTests
{
    [Fact]
    public async Task ABuildThatIsDetected_IsInFlight()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();

        Assert.True(await new ActivityEnvironment().RunAsync(() => rig.Activities.IsInFlightAsync(BuildId)));
    }

    [Theory]
    [InlineData(LivemapBuildState.Done)]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Parked)]
    [InlineData(LivemapBuildState.Superseded)]
    public async Task ABuildInAnyOtherState_IsNotInFlight(LivemapBuildState state)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(state);

        Assert.False(await new ActivityEnvironment().RunAsync(() => rig.Activities.IsInFlightAsync(BuildId)));
    }

    [Fact]
    public async Task ABuildWithNoRow_IsNotInFlight()
    {
        var rig = new LivemapBuildRig();

        Assert.False(await new ActivityEnvironment().RunAsync(() => rig.Activities.IsInFlightAsync(BuildId)));
    }

    [Theory]
    [InlineData(LivemapWorkflowFile.ExeCapture, FakeLivemapGitHub.ExeCaptureRunId)]
    [InlineData(LivemapWorkflowFile.Compat, FakeLivemapGitHub.CompatRunId)]
    public async Task ADispatch_AsksForTheBuildAndItsManifest_AndReturnsTheRunId(LivemapWorkflowFile workflow, long runId)
    {
        var rig = new LivemapBuildRig();

        var id = await new ActivityEnvironment().RunAsync(() => rig.Activities.DispatchAsync(new LivemapDispatchInput(workflow, BuildId, Gid)));

        Assert.Equal(runId, id);
        Assert.Equal((workflow, BuildId, Gid), Assert.Single(rig.GitHub.Dispatches));
    }

    [Fact]
    public async Task AFailedDispatch_Throws()
    {
        var rig = new LivemapBuildRig();
        rig.GitHub.DispatchFailure = new InvalidOperationException("refused");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ActivityEnvironment().RunAsync(() => rig.Activities.DispatchAsync(new LivemapDispatchInput(LivemapWorkflowFile.Compat, BuildId, Gid))));
    }

    [Fact]
    public async Task ReadingARun_ReturnsWhatGitHubSays()
    {
        var rig = new LivemapBuildRig();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.ExeCaptureRunId, new LivemapRun(FakeLivemapGitHub.ExeCaptureRunId, "in_progress", null, Sha));

        var run = await new ActivityEnvironment().RunAsync(() => rig.Activities.GetRunAsync(FakeLivemapGitHub.ExeCaptureRunId));
        var missing = await new ActivityEnvironment().RunAsync(() => rig.Activities.GetRunAsync(1));

        Assert.Equal(new LivemapRun(FakeLivemapGitHub.ExeCaptureRunId, "in_progress", null, Sha), run);
        Assert.Null(missing);
    }

    [Fact]
    public async Task AKnownResult_ClassifiesTheBuildAsKnown_AndKeepsTheRun()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Classified, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Classified, row.State);
        Assert.Equal(LivemapClassification.Known, row.Classification);
        Assert.Equal(FakeLivemapGitHub.CompatRunId, row.CompatRunId);
    }

    [Fact]
    public async Task AnEasyResult_ClassifiesTheBuildAsEasy()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = EasyArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Classified, outcome);
        Assert.Equal(LivemapClassification.Easy, (await rig.RowAsync()).Classification);
    }

    [Fact]
    public async Task TheForkBuildsFile_IsReadAtTheCommitTheRunUsed()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        await rig.ClassifyAsync();

        Assert.Equal(Sha, rig.GitHub.BuildsReadAt);
    }

    [Fact]
    public async Task AHardResult_SendsTheBuildToTheOwner_WithTheReasons()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = HardArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("site movement matched 0 times", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultThatDoesNotCheckOut_SendsTheBuildToTheOwner()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = new CompatArtifact(ReportJson("known", ExeSha, 2000, "0x2000", "300"), ForkBuilds);

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AnArtifactThatBreaksItsLimits_SendsTheBuildToTheOwner_WithoutAnotherTry()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.ArtifactFailure = new CompatArtifactException("The compat artifact holds an unexpected file.");

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("unexpected file", row.Detail, StringComparison.Ordinal);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task AForkBuildsFileOverItsLimit_SendsTheBuildToTheOwner()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();
        rig.GitHub.ForkBuildsFailure = new CompatArtifactException("The builds file is over the size cap.");

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, await rig.ClassifyAsync());
        Assert.Contains("size cap", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArtifactThatBreaksItsLimits_ForABuildThatNoLongerNeedsIt_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.Superseded);
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.ArtifactFailure = new CompatArtifactException("The compat artifact holds an unexpected file.");

        Assert.Equal(LivemapBuildOutcome.Superseded, await rig.ClassifyAsync());
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AReadThatFailsForAnotherReason_IsNotTakenForAnInvalidArtifact()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.ArtifactFailure = new InvalidOperationException("GitHub is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.ClassifyAsync());
        Assert.Equal(LivemapBuildState.Detected, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AResultForABuildThatNoLongerNeedsIt_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.Superseded);
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AHardResultForABuildThatNoLongerNeedsIt_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.Superseded);
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = HardArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    public async Task ACompatRunThatDidNotSucceed_ParksTheBuild(string conclusion)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId, conclusion));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Parked, row.State);
        Assert.Contains(conclusion, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompatRunThatIsNotFinished_ParksTheBuild()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, new LivemapRun(FakeLivemapGitHub.CompatRunId, "in_progress", null, Sha));
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        Assert.Equal(LivemapBuildOutcome.Parked, await rig.ClassifyAsync());
    }

    [Fact]
    public async Task ACompatRunThatCannotBeFound_ParksTheBuild()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = KnownArtifact();

        Assert.Equal(LivemapBuildOutcome.Parked, await rig.ClassifyAsync());
        Assert.Contains("found", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRunWithNoArtifact_ParksTheBuild()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.CompatRunId, Finished(FakeLivemapGitHub.CompatRunId));

        var outcome = await rig.ClassifyAsync();

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        Assert.Contains("artifact", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParkingTheBuild_CountsTheAttempt_AndSetsWhenToTryAgain()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.Activities.ParkAsync(new LivemapParkInput(BuildId, "the run timed out")));

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Equal("the run timed out", row.Detail);
        Assert.Equal(Now + LivemapBuildActivities.RetryAfter, row.NextRetryAt);
    }

    [Fact]
    public async Task ABuildThatKeepsFailing_GoesToTheOwnerAtTheCap()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        await rig.SetAttemptsAsync(LivemapBuildActivities.MaxAttempts - 1);

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.Activities.ParkAsync(new LivemapParkInput(BuildId, "still failing")));

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ParkingABuildThatIsNoLongerInFlight_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.Superseded);

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.Activities.ParkAsync(new LivemapParkInput(BuildId, "late")));

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Equal(0, (await rig.RowAsync()).Attempts);
    }
}
