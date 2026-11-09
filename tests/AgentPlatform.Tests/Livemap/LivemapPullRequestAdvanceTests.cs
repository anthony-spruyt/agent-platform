using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPullRequestAdvanceTests
{
    private static Task<bool> AdvanceAsync(LivemapBuildRig rig, TimeProvider? time = null) =>
        new ActivityEnvironment().RunAsync(() => rig.PullRequestActivities(time).AdvanceAsync(BuildId));

    [Fact]
    public async Task AnUntouchedPullRequestWithEveryRequiredCheckGreen_IsSquashMerged_AtTheHeadTheWorkerMade()
    {
        var rig = new LivemapBuildRig();
        var (number, head) = await rig.OpenedAsync();

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Equal((number, head, $"fix(builds): add server build 400 (#{number})"), Assert.Single(rig.PullRequests.Merges));
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AnEasyBuild_IsMergedUnderItsOwnSubject()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync(LivemapClassification.Easy);

        await AdvanceAsync(rig);

        Assert.Equal($"fix(builds): support server build 400 (#{number})", Assert.Single(rig.PullRequests.Merges).Title);
    }

    [Fact]
    public async Task EverythingTheGateJudges_IsReadForTheHeadTheWorkerMade()
    {
        var rig = new LivemapBuildRig();
        var (_, head) = await rig.OpenedAsync();
        rig.PullRequests.Calls.Clear();

        await AdvanceAsync(rig);

        Assert.Equal([head], rig.PullRequests.CheckRunReads.Distinct());
        Assert.Contains($"parents:{head}", rig.PullRequests.Calls);
        Assert.Contains($"mode:{head}", rig.PullRequests.Calls);
        Assert.Contains($"mode:{rig.PullRequests.MainHead}", rig.PullRequests.Calls);
    }

    [Fact]
    public async Task AMergeThatWasBegunAndDidNotFinish_IsFinishedByTheNextRun()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        Assert.True(await rig.Store.BeginMergeAsync(BuildId, Ct));

        await AdvanceAsync(rig);

        Assert.Single(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WithAutoMergeTurnedOff_AnOpenPullRequestIsHeld_AndNothingIsRead()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        rig.Config.AutoMerge = false;
        rig.PullRequests.Calls.Clear();

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Calls);
        Assert.Equal(LivemapBuildState.PullRequestWaiting, (await rig.RowAsync()).State);
    }

    [Theory]
    [InlineData(LivemapBuildState.Detected)]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestWaiting)]
    [InlineData(LivemapBuildState.Merged)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Parked)]
    [InlineData(LivemapBuildState.Superseded)]
    public async Task ABuildWithNoPullRequestToWatch_IsLeftAlone(LivemapBuildState state)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(state);

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Calls);
        Assert.Equal(state, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AnOpenBuildWithNoPullRequestRecorded_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync(LivemapBuildState.PullRequestOpen);

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APullRequestThatIsGone_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.Pulls.Remove(number);

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APullRequestClosedWithoutMerging_NeedsTheOwner_AndIsNotTouched()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.Pulls[number] = rig.PullRequests.Pulls[number] with { State = "closed" };
        rig.PullRequests.Calls.Clear();

        await AdvanceAsync(rig);

        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("closed", row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal(number, row.PullRequestNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APullRequestAlreadyMergedAtTheWorkersHead_EndsTheBuildMerged_WithoutMergingAgain(bool merging)
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        if (merging)
        {
            Assert.True(await rig.Store.BeginMergeAsync(BuildId, Ct));
        }

        rig.PullRequests.Pulls[number] = rig.PullRequests.Pulls[number] with { State = "closed", Merged = true };

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APullRequestMergedAtAnotherCommit_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.Pulls[number] = rig.PullRequests.Pulls[number] with { State = "closed", Merged = true, HeadSha = new string('7', 40) };

        await AdvanceAsync(rig);

        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ARequiredCheckThatHasNotFinished_KeepsTheBuildWaiting()
    {
        var rig = new LivemapBuildRig();
        var (_, head) = await rig.OpenedAsync();
        rig.PullRequests.Checks[head] = [.. GreenChecks().Take(1)];

        var pending = await AdvanceAsync(rig);

        Assert.True(pending);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.PullRequestOpen, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ARequiredCheckThatNeverFinishes_NeedsTheOwner_AfterTheLimit_WithItsName()
    {
        var rig = new LivemapBuildRig();
        var (number, head) = await rig.OpenedAsync();
        rig.PullRequests.Checks[head] = [.. GreenChecks().Take(1)];

        var pending = await AdvanceAsync(rig, new FixedTimeProvider(Now + LivemapPullRequestActivities.ChecksLimit + TimeSpan.FromMinutes(1)));

        Assert.False(pending);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains(RequiredChecks[1], row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal(number, row.PullRequestNumber);
    }

    [Fact]
    public async Task JustInsideTheLimit_ARequiredCheckStillGetsToFinish()
    {
        var rig = new LivemapBuildRig();
        var (_, head) = await rig.OpenedAsync();
        rig.PullRequests.Checks[head] = [.. GreenChecks().Take(1)];

        var pending = await AdvanceAsync(rig, new FixedTimeProvider(Now + LivemapPullRequestActivities.ChecksLimit - TimeSpan.FromMinutes(1)));

        Assert.True(pending);
    }

    [Fact]
    public async Task ARequiredCheckThatFailed_NeedsTheOwner_NamesTheCheck_AndLeavesThePullRequestOpen()
    {
        var rig = new LivemapBuildRig();
        var (number, head) = await rig.OpenedAsync();
        rig.PullRequests.Checks[head] = [new LivemapCheckRun(RequiredChecks[0], "completed", "failure", "github-actions"), .. GreenChecks().Skip(1)];

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains(RequiredChecks[0], row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(RequiredChecks[1], row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal("open", rig.PullRequests.Pulls[number].State);
        Assert.Equal(number, row.PullRequestNumber);
    }

    [Fact]
    public async Task APullRequestThatChangesMoreThanTheBuildsFile_NeedsTheOwner_WithTheReason()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.Files[number].Add(new LivemapPullRequestFile("README.md", "modified", null));

        await AdvanceAsync(rig);

        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("data/builds.json", row.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.PullRequests.Merges);
    }

    [Fact]
    public async Task WhenMainHasMovedOn_ThePullRequestIsMadeAgainOnTheNewMain_NotMerged()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        rig.PullRequests.MainHead = new string('d', 40);

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Merges);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Classified, 1), (row.State, row.Attempts));
        Assert.Null(row.PullRequestNumber);
        Assert.Null(row.HeadSha);
        Assert.Equal(LivemapClassification.Known, row.Classification);
        Assert.Equal(FakeLivemapGitHub.CompatRunId, row.CompatRunId);
    }

    [Fact]
    public async Task WhenMainKeepsMovingOn_TheOwnerIsAskedAtTheAttemptCap()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        await rig.SetAttemptsAsync(LivemapBuildActivities.MaxAttempts - 1);
        rig.PullRequests.MainHead = new string('d', 40);

        await AdvanceAsync(rig);

        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ABuildSupersededWhileItsChecksWereRead_IsNotMerged()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        rig.PullRequests.Hook = async call =>
        {
            if (call.StartsWith("checks:", StringComparison.Ordinal))
            {
                await rig.Store.SupersedeOlderAsync(BuildId + 1, Ct);
            }
        };

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        Assert.Empty(rig.PullRequests.Merges);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_WithMainWhereItWas_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.MergeResult = LivemapMergeResult.Refused;

        var pending = await AdvanceAsync(rig);

        Assert.False(pending);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Equal(number, row.PullRequestNumber);
        Assert.Empty(rig.PullRequests.Closed);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_BecauseItWasMergedAlreadyAtTheWorkersHead_EndsMerged()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.MergeResult = LivemapMergeResult.Refused;
        rig.PullRequests.Hook = call =>
        {
            if (call.StartsWith("merge:", StringComparison.Ordinal))
            {
                rig.PullRequests.Pulls[number] = rig.PullRequests.Pulls[number] with { State = "closed", Merged = true };
            }

            return Task.CompletedTask;
        };

        await AdvanceAsync(rig);

        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_ThatLeftThePullRequestMergedAtAnotherCommit_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        var (number, _) = await rig.OpenedAsync();
        rig.PullRequests.MergeResult = LivemapMergeResult.Refused;
        rig.PullRequests.Hook = call =>
        {
            if (call.StartsWith("merge:", StringComparison.Ordinal))
            {
                rig.PullRequests.Pulls[number] = rig.PullRequests.Pulls[number] with { State = "closed", Merged = true, HeadSha = new string('7', 40) };
            }

            return Task.CompletedTask;
        };

        await AdvanceAsync(rig);

        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_BecauseMainMovedOnMeanwhile_IsMadeAgainOnTheNewMain()
    {
        var rig = new LivemapBuildRig();
        await rig.OpenedAsync();
        rig.PullRequests.MergeResult = LivemapMergeResult.Refused;
        rig.PullRequests.Hook = call =>
        {
            if (call.StartsWith("merge:", StringComparison.Ordinal))
            {
                rig.PullRequests.MainHead = new string('d', 40);
            }

            return Task.CompletedTask;
        };

        await AdvanceAsync(rig);

        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Classified, 1), (row.State, row.Attempts));
        Assert.Null(row.PullRequestNumber);
    }
}
