using AgentPlatform.Host.Livemap;
using Microsoft.EntityFrameworkCore;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapShipRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapShipPinTests
{
    private static readonly string _branch = "livemap/pin-" + Tag;

    private static readonly Dictionary<string, Action<LivemapShipRig>> _refusals = new()
    {
        { "another file beside the pin", rig => rig.Pin.Files[PinNumber].Add(new LivemapPullRequestFile("README.md", "modified", null, "@@")) },
        { "the encrypted file beside the pin", rig => rig.Pin.Files[PinNumber].Add(new LivemapPullRequestFile("apps/livemap/secrets.sops.yaml", "modified", null, "@@")) },
        { "another file instead of the pin", rig => rig.Pin.Files[PinNumber] = [new LivemapPullRequestFile("apps/other/pin.yaml", "modified", null, "@@")] },
        { "two commits", rig => rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { Commits = 2 } },
        { "a head the worker did not make", rig => rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { HeadSha = new string('9', 40) } },
        { "another author", rig => rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { AuthorLogin = "someone[bot]" } },
        { "a draft", rig => rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { Draft = true } },
    };

    public static TheoryData<string> Refusals()
    {
        var data = new TheoryData<string>();
        foreach (var point in _refusals.Keys)
        {
            data.Add(point);
        }

        return data;
    }

    public static TheoryData<string, string?> PinsThatCannotBeEdited => new()
    {
        { "no pin file", null },
        { "no version line", "LIVEMAP_SHA256: \"" + OldDigest + "\"\n" },
        { "no digest line", "LIVEMAP_VERSION: \"" + OldTag + "\"\n" },
    };

    private static void MoveMain(LivemapShipRig rig, char sha)
    {
        rig.Pin.MainHead = new string(sha, 40);
        rig.Pin.Contents[(rig.Pin.MainHead, PinPath)] = PinFile;
    }

    [Fact]
    public async Task TheVerifiedRelease_IsPinnedInOneCommit_ThroughAPullRequest()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var commit = Assert.Single(rig.Pin.Commits);
        Assert.Equal((_branch, FakeLivemapPullRequests.StartMain, "chore(enshrouded): update livemap to v2.0.0", PinPath), (commit.Branch, commit.ExpectedHeadOid, commit.Headline, commit.Path));
        Assert.Equal(PinFile.Replace(OldTag, Tag, StringComparison.Ordinal).Replace(OldDigest, FakeLivemapReleases.ZipSha, StringComparison.Ordinal), commit.Content);
        var opened = Assert.Single(rig.Pin.Opened);
        Assert.Equal((_branch, PinTitle), (opened.Branch, opened.Title));
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.PinOpen, row.State);
        Assert.Equal(opened.Number, row.PinPullRequestNumber);
        Assert.Equal(commit.Oid, row.PinHeadSha);
    }

    [Fact]
    public async Task ThePinPullRequest_LinksTheRelease_AndTheTrackingIssueByItsFullAddress()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);

        await rig.ShipAsync();

        var body = Assert.Single(rig.Pin.Opened).Body;
        Assert.Contains("https://github.com/octo/livemap/releases/tag/v2.0.0", body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/issues/12", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#12", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePinFile_IsReadOnceByItsOneExactPath_AtTheHeadOfMain_AndNothingGoesThroughTheFork()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);

        await rig.ShipAsync();

        Assert.Equal([$"read:{PinPath}@{FakeLivemapPullRequests.StartMain}"], rig.Pin.Calls.Where(call => call.StartsWith("read:", StringComparison.Ordinal)));
        Assert.Empty(rig.Fork.Calls);
        Assert.Empty(rig.Fork.Commits);
    }

    [Fact]
    public async Task WithAutoMergeOff_ThePinPullRequestIsOpened_AndTheBuildWaitsForTheSwitch()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        rig.Config.AutoMerge = false;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Single(rig.Pin.Opened);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal(LivemapBuildState.PinWaiting, (await rig.RowAsync()).State);
    }

    [Theory]
    [InlineData("v2.0.0")]
    [InlineData("v2.0.1")]
    [InlineData("v10.0.0")]
    public async Task APinAtOrAheadOfTheRelease_IsLeftAlone_AndTheBuildIsPinned(string pinned)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        rig.Pin.Contents[(FakeLivemapPullRequests.StartMain, PinPath)] = PinFile.Replace(OldTag, pinned, StringComparison.Ordinal);

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Pin.Commits);
        Assert.Empty(rig.Pin.Opened);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Pinned, row.State);
        Assert.Null(row.PinPullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(PinsThatCannotBeEdited))]
    public async Task APinFileTheWorkerCannotEdit_IsFlaggedForAPerson_AndNothingIsWritten(string point, string? content)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        if (content is null)
        {
            rig.Pin.Contents.Clear();
        }
        else
        {
            rig.Pin.Contents[(FakeLivemapPullRequests.StartMain, PinPath)] = content;
        }

        var wait = await rig.ShipAsync();

        Assert.False(wait, point);
        Assert.Empty(rig.Pin.Commits);
        Assert.Empty(rig.Pin.Branches);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.False(string.IsNullOrWhiteSpace(row.Detail), point);
    }

    [Fact]
    public async Task ABuildWithoutAReleaseDigest_IsFlagged_NotPinnedToNothing()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        await using (var db = await rig.Data.Factory.CreateDbContextAsync(Ct))
        {
            var row = await db.LivemapBuilds.SingleAsync(Ct);
            row.ReleaseSha256 = null;
            await db.SaveChangesAsync(Ct);
        }

        await rig.ShipAsync();

        Assert.Empty(rig.Pin.Commits);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WhatAnEarlierAttemptLeftOnThePinBranch_IsClearedBeforeThePinIsMade()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        var leftover = rig.Pin.Leftover(_branch);

        await rig.ShipAsync();

        Assert.Equal([leftover.Number], rig.Pin.Closed);
        Assert.Equal([_branch], rig.Pin.Deleted);
        Assert.Single(rig.Pin.Commits);
        Assert.Equal(LivemapBuildState.PinOpen, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequest_ForABuildWhoseStateMovedMeanwhile_IsClosedAgain()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Verified);
        rig.Pin.Hook = async call =>
        {
            if (call.StartsWith("open:", StringComparison.Ordinal))
            {
                Assert.True(await rig.Store.FlagPullRequestAsync(BuildId, "A person moved it.", Ct));
            }
        };

        await rig.ShipAsync();

        var opened = Assert.Single(rig.Pin.Opened);
        Assert.Equal([opened.Number], rig.Pin.Closed);
        Assert.Equal(2, rig.Pin.Deleted.Count);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AGreenPinPullRequest_IsSquashMerged_AtTheHeadTheWorkerMade()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal((PinNumber, PinHead, PinTitle + " (#88)"), Assert.Single(rig.Pin.Merges));
        Assert.Empty(rig.Fork.Merges);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Pinned, row.State);
        Assert.Equal(Now, row.PinnedAt);
    }

    [Fact]
    public async Task WithAutoMergeOff_ThePinPullRequestIsNotMerged_AndTheBuildWaitsWhereThePollResumesIt()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Config.AutoMerge = false;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal(LivemapBuildState.PinWaiting, (await rig.RowAsync()).State);
        Assert.True(await rig.Store.ResumeWaitingAsync(BuildId, Ct));
        Assert.Equal(LivemapBuildState.PinOpen, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestMergedByHand_AtTheWorkersHead_CountsAsPinned_EvenWithAutoMergeOff()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.Pin.Pulls[PinNumber] = rig.SeedPinPullRequest() with { State = "closed", Merged = true };
        rig.Config.AutoMerge = false;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal(LivemapBuildState.Pinned, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestMergedAtAnotherCommit_IsFlagged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.Pin.Pulls[PinNumber] = rig.SeedPinPullRequest() with { State = "closed", Merged = true, HeadSha = new string('9', 40) };

        await rig.ShipAsync();

        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestClosedWithoutAMerge_IsFlagged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.Pin.Pulls[PinNumber] = rig.SeedPinPullRequest() with { State = "closed" };

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestThatCannotBeFound_IsFlagged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);

        await rig.ShipAsync();

        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task APinPullRequestThatIsNotExactlyTheWorkersPinCommit_IsClosedUnmerged_AndFlaggedForAPerson(string point)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        _refusals[point](rig);

        var wait = await rig.ShipAsync();

        Assert.False(wait, point);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal([PinNumber], rig.Pin.Closed);
        Assert.Equal([_branch], rig.Pin.Deleted);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.False(string.IsNullOrWhiteSpace(row.Detail), point);
    }

    [Fact]
    public async Task WhenMainHasMovedOn_ThePinPullRequestIsClosed_AndThePinIsMadeAgainOnTheNewMain()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        MoveMain(rig, 'e');

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Pin.Merges);
        Assert.Equal([PinNumber], rig.Pin.Closed);
        Assert.Equal([_branch], rig.Pin.Deleted);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Verified, row.State);
        Assert.Equal(1, row.PinAttempts);
        Assert.Null(row.PinPullRequestNumber);

        await rig.ShipAsync();

        Assert.Equal(new string('e', 40), Assert.Single(rig.Pin.Commits).ExpectedHeadOid);
    }

    [Fact]
    public async Task WhenMainMovesOnASecondTime_ThePinIsNotMadeAThirdTime_AndAPersonIsAsked()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        MoveMain(rig, 'e');
        await rig.ShipAsync();
        await rig.ShipAsync();
        MoveMain(rig, 'f');

        await rig.ShipAsync();

        Assert.Empty(rig.Pin.Merges);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Equal(2, row.PinAttempts);
        Assert.Equal(2, rig.Pin.Closed.Count);
    }

    [Fact]
    public async Task WhenMainMovesAfterThePullRequestWasJudged_NothingIsMerged_AndThePinIsMadeAgain()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.Hook = call =>
        {
            if (call == $"files:{PinNumber}")
            {
                rig.Pin.MainHead = new string('e', 40);
            }

            return Task.CompletedTask;
        };

        await rig.ShipAsync();

        Assert.Empty(rig.Pin.Merges);
        Assert.Equal([PinNumber], rig.Pin.Closed);
        Assert.Equal(LivemapBuildState.Verified, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ANotMergeableAnswer_IsAskedAgain_ThreeTimes_FiveSecondsApart()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.NotMergeableAnswers = 2;

        await rig.ShipAsync();

        Assert.Equal(3, rig.Pin.Merges.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], rig.Time.Waits);
        Assert.Equal(LivemapBuildState.Pinned, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestThatStaysNotMergeable_IsClosed_AndThePinIsMadeAgain()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.NotMergeableAnswers = 99;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(4, rig.Pin.Merges.Count);
        Assert.Equal(3, rig.Time.Waits.Count);
        Assert.Equal([PinNumber], rig.Pin.Closed);
        Assert.Equal([_branch], rig.Pin.Deleted);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Verified, row.State);
        Assert.Equal(1, row.PinAttempts);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_ClosesThePinPullRequest_AndNamesTheRefusal()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.MergeResult = LivemapMergeResult.Refused;

        await rig.ShipAsync();

        Assert.Single(rig.Pin.Merges);
        Assert.Equal([PinNumber], rig.Pin.Closed);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Verified, row.State);
        Assert.Contains("refused", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMergeWhoseAnswerWasLost_IsRecognisedWhenThePinPullRequestIsMergedAtTheSameHead()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.MergeResult = LivemapMergeResult.Refused;
        rig.Pin.Hook = call =>
        {
            if (call == $"merge:{PinNumber}")
            {
                rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { State = "closed", Merged = true };
            }

            return Task.CompletedTask;
        };

        await rig.ShipAsync();

        Assert.Empty(rig.Pin.Closed);
        Assert.Equal(LivemapBuildState.Pinned, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APinPullRequestMergedAtAnotherHead_IsNotTakenForTheWorkersMerge()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.PinOpen);
        rig.SeedPinPullRequest();
        rig.Pin.MergeResult = LivemapMergeResult.Refused;
        rig.Pin.Hook = call =>
        {
            if (call == $"merge:{PinNumber}")
            {
                rig.Pin.Pulls[PinNumber] = rig.Pin.Pulls[PinNumber] with { State = "closed", Merged = true, HeadSha = "someone-elses-head" };
            }

            return Task.CompletedTask;
        };

        await rig.ShipAsync();

        Assert.NotEqual(LivemapBuildState.Pinned, (await rig.RowAsync()).State);
    }
}
