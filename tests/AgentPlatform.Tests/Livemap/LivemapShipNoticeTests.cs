using AgentPlatform.Host.Livemap;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapShipRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapShipNoticeTests
{
    [Fact]
    public async Task AShippedBuild_GetsOneNotice_OnTheTrackingIssue_AndIsDone()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var post = Assert.Single(rig.Notices.Posts);
        Assert.Equal((BuildId, FakeLivemapPullRequests.AppLogin, Now), (post.BuildId, post.AppLogin, post.Since));
        Assert.Contains("shipped", post.Body, StringComparison.Ordinal);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Done, row.State);
        Assert.Equal(Now, row.NotifiedAt);
    }

    [Fact]
    public async Task TheNotice_LinksTheReleaseInTheFork_AndThePinPullRequestInThePinRepository()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);

        await rig.ShipAsync();

        var body = Assert.Single(rig.Notices.Posts).Body;
        Assert.Contains("https://github.com/octo/livemap/releases/tag/v2.0.0", body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/pull/41", body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/cluster/pull/88", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoticeAlreadyPosted_WhenTheAnswerWasLost_IsNotPostedAgain_AndTheBuildIsDone()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);
        rig.Notices.Posts.Add((BuildId, FakeLivemapPullRequests.AppLogin, Now, "earlier"));

        await rig.ShipAsync();

        Assert.Single(rig.Notices.Posts);
        Assert.Equal(LivemapBuildState.Done, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ANoticeThatCannotBePosted_LeavesTheBuildPinned_ForTheNextRunToPost()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);
        rig.Notices.Failure = new InvalidOperationException("GitHub is down.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.ShipAsync());

        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Pinned, row.State);
        Assert.Null(row.NotifiedAt);

        rig.Notices.Failure = null;
        await rig.ShipAsync();

        Assert.Single(rig.Notices.Posts);
        Assert.Equal(LivemapBuildState.Done, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ABuildThatNeedsAPerson_GetsItsNoticeOnce_AndStaysWithTheOwner()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        Assert.True(await rig.Store.FlagPullRequestAsync(BuildId, "The release did not pass its checks.", Ct));

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var post = Assert.Single(rig.Notices.Posts);
        Assert.Contains("needs a person", post.Body, StringComparison.Ordinal);
        Assert.Contains("The release did not pass its checks.", post.Body, StringComparison.Ordinal);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Equal(Now, row.NotifiedAt);

        await rig.ShipAsync();

        Assert.Equal(1, rig.Notices.Asked);
    }

    [Fact]
    public async Task ACodePullRequest_GetsItsNoticeOnce_AndWaitsForTheOwnerWithoutBeingMerged()
    {
        var rig = new LivemapShipRig();
        await rig.AtOwnerReviewAsync(number: 52);

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var post = Assert.Single(rig.Notices.Posts);
        Assert.Contains("waits for the owner", post.Body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/pull/52", post.Body, StringComparison.Ordinal);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.OwnerReview, row.State);
        Assert.Equal(Now, row.NotifiedAt);

        await rig.ShipAsync();

        Assert.Equal(1, rig.Notices.Asked);
        Assert.DoesNotContain(rig.Fork.Calls, call => call.StartsWith("merge:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFlagRaisedByTheShipStep_IsNoticedOnTheNextRun()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "completed", "failure", 2, FakeLivemapReleases.TagCommit, "https://github.example/run");

        await rig.ShipAsync();
        Assert.Empty(rig.Notices.Posts);
        await rig.ShipAsync();

        Assert.Contains("twice", Assert.Single(rig.Notices.Posts).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatIsDone_NeedsNothing()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Pinned);
        Assert.True(await rig.Store.NotifiedAsync(BuildId, Ct));

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(0, rig.Notices.Asked);
        Assert.Empty(rig.Fork.Calls);
        Assert.Empty(rig.Pin.Calls);
        Assert.Empty(rig.Releases.Calls);
    }

    [Fact]
    public async Task ABuildWaitingForTheSwitch_IsLeftAlone()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        Assert.True(await rig.Store.HoldAsync(BuildId, Ct));

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(LivemapBuildState.ReleaseWaiting, (await rig.RowAsync()).State);
        Assert.Empty(rig.Fork.Calls);
        Assert.Empty(rig.Releases.Calls);
    }

    [Fact]
    public async Task ABuildThatIsNotKnown_NeedsNothing()
    {
        var rig = new LivemapShipRig();

        var wait = await new ActivityEnvironment().RunAsync(() => rig.Activities().ShipAsync(999));

        Assert.False(wait);
    }
}
