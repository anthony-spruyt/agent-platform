using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildStoreShipTests
{
    private const string Gid = "8886672487109033979";

    private const long Build = 26430253;

    private const int Pr = 42;

    private const int PinPr = 7;

    private const string Tag = "v1.10.0";

    private static readonly string Head = new('a', 40);

    private static readonly string PinHead = new('b', 40);

    private static readonly string Sha256 = new('c', 64);

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LivemapBuildState[] ShipPhase =
    [
        LivemapBuildState.Merged,
        LivemapBuildState.Released,
        LivemapBuildState.Verified,
        LivemapBuildState.PinOpen,
        LivemapBuildState.Pinned,
    ];

    private static readonly LivemapBuildState[] Waiting = [LivemapBuildState.ReleaseWaiting, LivemapBuildState.PinWaiting];

    public static TheoryData<LivemapBuildState> ShipStates => new(ShipPhase);

    public static TheoryData<LivemapBuildState> WaitingStates => new(Waiting);

    public static TheoryData<LivemapBuildState> StatesThatMayBeHeld => new(
        Enum.GetValues<LivemapBuildState>().Where(s => s is LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging or LivemapBuildState.Merged or LivemapBuildState.PinOpen));

    public static TheoryData<LivemapBuildState> StatesOtherThanMerged => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Merged));

    public static TheoryData<LivemapBuildState> StatesOtherThanReleased => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Released));

    public static TheoryData<LivemapBuildState> StatesOtherThanVerified => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Verified));

    public static TheoryData<LivemapBuildState> StatesThatMayNotBePinned => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.Verified or LivemapBuildState.PinOpen)));

    public static TheoryData<LivemapBuildState> StatesThatMayNotRedoThePin => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.PinOpen or LivemapBuildState.PinWaiting)));

    public static TheoryData<LivemapBuildState> StatesThatMayNotBeNotified => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.Pinned or LivemapBuildState.NeedsOwner)));

    private static LivemapBuildStore Store(InMemoryRunData data, DateTimeOffset? at = null) => new(data.Factory, new FixedTimeProvider(at ?? Now));

    private static async Task<LivemapBuild> ReadAsync(InMemoryRunData data, long buildId = Build) => (await Store(data).FindAsync(buildId, Ct))!;

    private static async Task<InMemoryRunData> SeedAsync(LivemapBuildState state, long buildId = Build, InMemoryRunData? data = null)
    {
        data ??= InMemoryRunData.Create();
        var store = Store(data, Now.AddHours(-1));
        if (!ShipPhase.Contains(state) && !Waiting.Contains(state))
        {
            await store.RecordAsync(buildId, Gid, SteamUpdated, state, Ct);
            return data;
        }

        await store.RecordAsync(buildId, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        Assert.True(await store.ClassifyAsync(buildId, LivemapClassification.Easy, 777, Ct));
        Assert.True(await store.OpenPullRequestAsync(buildId, Pr, Head, waiting: false, Ct));
        Assert.True(await store.BeginMergeAsync(buildId, Ct));
        Assert.True(await store.MergedAsync(buildId, Ct));
        if (state == LivemapBuildState.ReleaseWaiting)
        {
            Assert.True(await store.HoldAsync(buildId, Ct));
        }

        if (state is LivemapBuildState.Merged or LivemapBuildState.ReleaseWaiting)
        {
            return data;
        }

        Assert.True(await store.ReleasedAsync(buildId, Tag, Ct));
        if (state == LivemapBuildState.Released)
        {
            return data;
        }

        Assert.True(await store.VerifiedAsync(buildId, Sha256, Ct));
        if (state == LivemapBuildState.Verified)
        {
            return data;
        }

        Assert.True(await store.OpenPinAsync(buildId, PinPr, PinHead, waiting: state == LivemapBuildState.PinWaiting, Ct));
        if (state == LivemapBuildState.Pinned)
        {
            Assert.True(await store.PinnedAsync(buildId, Ct));
        }

        return data;
    }

    [Fact]
    public async Task AMergedBuild_KeepsWhenItMerged()
    {
        var data = await SeedAsync(LivemapBuildState.Merged);

        Assert.Equal(Now.AddHours(-1), (await ReadAsync(data)).MergedAt);
    }

    [Theory]
    [InlineData(LivemapBuildState.Merged, LivemapBuildState.ReleaseWaiting, null)]
    [InlineData(LivemapBuildState.PinOpen, LivemapBuildState.PinWaiting, PinPr)]
    public async Task AShipStepThatNeedsTheSwitch_Waits_UntilItTurnsOn(LivemapBuildState state, LivemapBuildState waiting, int? pinNumber)
    {
        var data = await SeedAsync(state);

        Assert.True(await Store(data).HoldAsync(Build, Ct));
        Assert.Equal(waiting, (await ReadAsync(data)).State);

        Assert.True(await Store(data).ResumeWaitingAsync(Build, Ct));
        var resumed = await ReadAsync(data);
        Assert.Equal(state, resumed.State);
        Assert.Equal(pinNumber, resumed.PinPullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayBeHeld))]
    public async Task ABuildAtAMerge_CanBeHeld(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.True(await Store(data).HoldAsync(Build, Ct));
        Assert.NotEqual(state, (await ReadAsync(data)).State);
    }

    [Fact]
    public async Task AMergedBuild_WhoseReleaseIsOut_KeepsTheTagAndWhenItCame()
    {
        var data = await SeedAsync(LivemapBuildState.Merged);

        Assert.True(await Store(data, Now.AddMinutes(30)).ReleasedAsync(Build, Tag, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Released, build.State);
        Assert.Equal(Tag, build.ReleaseTag);
        Assert.Equal(Now.AddMinutes(30), build.ReleasedAt);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanMerged))]
    public async Task ABuildThatIsNotMerged_CannotGetARelease(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).ReleasedAsync(Build, "v9.9.9", Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.NotEqual("v9.9.9", build.ReleaseTag);
    }

    [Fact]
    public async Task AReleasedBuild_WhoseReleaseWasChecked_KeepsTheChecksum()
    {
        var data = await SeedAsync(LivemapBuildState.Released);

        Assert.True(await Store(data).VerifiedAsync(Build, Sha256, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Verified, build.State);
        Assert.Equal(Sha256, build.ReleaseSha256);
        Assert.Equal(Tag, build.ReleaseTag);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanReleased))]
    public async Task ABuildThatIsNotReleased_CannotBeVerified(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).VerifiedAsync(Build, new string('d', 64), Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.NotEqual(new string('d', 64), build.ReleaseSha256);
    }

    [Theory]
    [InlineData(false, LivemapBuildState.PinOpen)]
    [InlineData(true, LivemapBuildState.PinWaiting)]
    public async Task AVerifiedBuild_ThatGetsItsPinPullRequest_KeepsTheNumberAndTheHead(bool waiting, LivemapBuildState expected)
    {
        var data = await SeedAsync(LivemapBuildState.Verified);

        Assert.True(await Store(data).OpenPinAsync(Build, PinPr, PinHead, waiting, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(expected, build.State);
        Assert.Equal(PinPr, build.PinPullRequestNumber);
        Assert.Equal(PinHead, build.PinHeadSha);
        Assert.Equal(Pr, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanVerified))]
    public async Task ABuildThatIsNotVerified_CannotGetAPinPullRequest(LivemapBuildState state)
    {
        var data = await SeedAsync(state);
        var before = await ReadAsync(data);

        Assert.False(await Store(data).OpenPinAsync(Build, 99, new string('e', 40), waiting: false, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.Equal(before.PinPullRequestNumber, build.PinPullRequestNumber);
        Assert.Equal(before.PinHeadSha, build.PinHeadSha);
    }

    [Theory]
    [InlineData(LivemapBuildState.Verified)]
    [InlineData(LivemapBuildState.PinOpen)]
    public async Task APinnedBuild_KeepsWhenItWasPinned_WhetherOrNotThePinNeededAPullRequest(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.True(await Store(data, Now.AddMinutes(5)).PinnedAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Pinned, build.State);
        Assert.Equal(Now.AddMinutes(5), build.PinnedAt);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayNotBePinned))]
    public async Task ABuildWithNoPinInHand_CannotBePinned(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).PinnedAsync(Build, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Theory]
    [InlineData(LivemapBuildState.PinOpen)]
    [InlineData(LivemapBuildState.PinWaiting)]
    public async Task APinPullRequestThatIsGivenUp_SendsTheBuildBackToItsReleaseOnce(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        var next = await Store(data).RedoPinAsync(Build, "main moved", 2, Ct);

        Assert.Equal(LivemapBuildState.Verified, next);
        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Verified, build.State);
        Assert.Null(build.PinPullRequestNumber);
        Assert.Null(build.PinHeadSha);
        Assert.Equal(1, build.PinAttempts);
        Assert.Equal("main moved", build.Detail);
        Assert.Equal(Sha256, build.ReleaseSha256);
    }

    [Fact]
    public async Task APinPullRequestThatIsGivenUpTwice_NeedsTheOwner_WithItsPullRequestKept()
    {
        var data = await SeedAsync(LivemapBuildState.PinOpen);
        await Store(data).RedoPinAsync(Build, "first", 2, Ct);
        await Store(data).OpenPinAsync(Build, 8, PinHead, waiting: false, Ct);

        var next = await Store(data).RedoPinAsync(Build, "second", 2, Ct);

        Assert.Equal(LivemapBuildState.NeedsOwner, next);
        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.NeedsOwner, build.State);
        Assert.Equal(8, build.PinPullRequestNumber);
        Assert.Equal(2, build.PinAttempts);
        Assert.Equal("second", build.Detail);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayNotRedoThePin))]
    public async Task OnlyABuildWithAPinPullRequest_CanRedoIt(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.Null(await Store(data).RedoPinAsync(Build, "late", 2, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.Equal(0, build.PinAttempts);
    }

    [Theory]
    [MemberData(nameof(ShipStates))]
    public async Task ABuildAtAnyShipStep_CanBeFlaggedForTheOwner_WithItsColumnsKept(LivemapBuildState state)
    {
        var data = await SeedAsync(state);
        var before = await ReadAsync(data);

        Assert.True(await Store(data).FlagPullRequestAsync(Build, "refused", Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.NeedsOwner, build.State);
        Assert.Equal("refused", build.Detail);
        Assert.Equal(before.ReleaseTag, build.ReleaseTag);
        Assert.Equal(before.PinPullRequestNumber, build.PinPullRequestNumber);
    }

    [Fact]
    public async Task APinnedBuild_IsDone_OnceItsNoticeIsPosted()
    {
        var data = await SeedAsync(LivemapBuildState.Pinned);

        Assert.True(await Store(data, Now.AddMinutes(1)).NotifiedAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Done, build.State);
        Assert.Equal(Now.AddMinutes(1), build.NotifiedAt);
        Assert.False(await Store(data).NotifiedAsync(Build, Ct));
    }

    [Fact]
    public async Task ABuildThatNeedsTheOwner_IsNotifiedOnce_AndStaysThere()
    {
        var data = await SeedAsync(LivemapBuildState.NeedsOwner);

        Assert.True(await Store(data, Now.AddMinutes(1)).NotifiedAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.NeedsOwner, build.State);
        Assert.Equal(Now.AddMinutes(1), build.NotifiedAt);
        Assert.False(await Store(data).NotifiedAsync(Build, Ct));
    }

    [Theory]
    [MemberData(nameof(StatesThatMayNotBeNotified))]
    public async Task ABuildThatHasNothingToReport_IsNotNotified(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).NotifiedAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.Null(build.NotifiedAt);
    }

    [Theory]
    [MemberData(nameof(ShipStates))]
    public async Task AShipStepThatFailsForNow_ResumesWhereItWas(LivemapBuildState state)
    {
        var data = await SeedAsync(state);
        var before = await ReadAsync(data);

        var parked = await Store(data).ParkAsync(Build, "GitHub was down", 3, TimeSpan.FromMinutes(10), Ct);
        Assert.Equal(LivemapBuildState.Parked, parked);
        Assert.True(await Store(data, Now.AddMinutes(10)).ResumeAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.Equal(before.PullRequestNumber, build.PullRequestNumber);
        Assert.Equal(before.ReleaseTag, build.ReleaseTag);
        Assert.Equal(before.ReleaseSha256, build.ReleaseSha256);
        Assert.Equal(before.PinPullRequestNumber, build.PinPullRequestNumber);
        Assert.Equal(1, build.Attempts);
    }

    [Fact]
    public async Task AShipStepThatKeepsFailing_NeedsTheOwnerAtTheCap()
    {
        var data = await SeedAsync(LivemapBuildState.Released);
        var at = Now;
        var results = new List<LivemapBuildState?>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            results.Add(await Store(data, at).ParkAsync(Build, $"try {attempt}", 3, TimeSpan.FromMinutes(10), Ct));
            at = at.AddMinutes(11);
            await Store(data, at).ResumeAsync(Build, Ct);
        }

        Assert.Equal([LivemapBuildState.Parked, LivemapBuildState.Parked, LivemapBuildState.NeedsOwner], results);
    }

    [Fact]
    public async Task TheBuildsAtAShipStep_AreFoundOldestFirst_AndNoOtherBuild()
    {
        var data = InMemoryRunData.Create();
        long id = 100;
        var expected = new List<long>();
        foreach (var state in Enum.GetValues<LivemapBuildState>())
        {
            await SeedAsync(state, ++id, data);
            if (ShipPhase.Contains(state))
            {
                expected.Add(id);
            }
        }

        var found = await Store(data).FindShippingAsync(Ct);

        Assert.Equal(expected, found.Select(b => b.BuildId));
    }

    [Theory]
    [MemberData(nameof(WaitingStates))]
    public async Task AWaitingBuild_IsNotFoundAmongTheShippingOnes(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.Empty(await Store(data).FindShippingAsync(Ct));
    }

    [Fact]
    public async Task TheBuildsThatNeedTheOwner_AreFoundForANotice_UntilTheNoticeWentOut()
    {
        var data = InMemoryRunData.Create();
        await SeedAsync(LivemapBuildState.NeedsOwner, 100, data);
        await SeedAsync(LivemapBuildState.Detected, 150, data);
        await SeedAsync(LivemapBuildState.NeedsOwner, 200, data);

        Assert.Equal([100L, 200L], (await Store(data).FindUnnotifiedAsync(Ct)).Select(b => b.BuildId));

        await Store(data).NotifiedAsync(100, Ct);
        Assert.Equal([200L], (await Store(data).FindUnnotifiedAsync(Ct)).Select(b => b.BuildId));
    }

    [Theory]
    [MemberData(nameof(ShipStates))]
    [MemberData(nameof(WaitingStates))]
    public async Task ANewerBuild_NeverSupersedesABuildThatIsShipping(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.Equal(0, await Store(data).SupersedeOlderAsync(Build + 1, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }
}
