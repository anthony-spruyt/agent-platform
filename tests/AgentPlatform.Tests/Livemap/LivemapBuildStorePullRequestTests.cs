using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildStorePullRequestTests
{
    private const string Gid = "8886672487109033979";

    private const long Build = 26430253;

    private const int Pr = 42;

    private static readonly string Head = new('a', 40);

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LivemapBuildState[] PullRequestPhase = [LivemapBuildState.Classified, LivemapBuildState.PullRequestOpen, LivemapBuildState.Merging];

    public static TheoryData<LivemapBuildState> StatesOtherThanClassified => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Classified));

    public static TheoryData<LivemapBuildState> StatesOtherThanOpenOrMerging => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging)));

    public static TheoryData<LivemapBuildState> StatesOtherThanOpen => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.PullRequestOpen));

    public static TheoryData<LivemapBuildState> StatesOtherThanMerging => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Merging));

    public static TheoryData<LivemapBuildState> StatesOtherThanWaiting => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.PullRequestWaiting));

    public static TheoryData<LivemapBuildState> StatesOutsideThePullRequestPhase => new(Enum.GetValues<LivemapBuildState>().Where(s => !PullRequestPhase.Contains(s) && s != LivemapBuildState.Detected));

    public static TheoryData<LivemapBuildState> StatesThatMayRestart => new(LivemapBuildState.Classified, LivemapBuildState.PullRequestOpen, LivemapBuildState.PullRequestWaiting);

    public static TheoryData<LivemapBuildState> StatesThatMayReopen => new(LivemapBuildState.PullRequestOpen, LivemapBuildState.Merging);

    public static TheoryData<LivemapBuildState> StatesThatMayNotReopen => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging)));

    public static TheoryData<LivemapBuildState> StatesThatMayNotRestart => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.Classified or LivemapBuildState.PullRequestOpen or LivemapBuildState.PullRequestWaiting)));

    private static LivemapBuildStore Store(InMemoryRunData data, DateTimeOffset? at = null) => new(data.Factory, new FixedTimeProvider(at ?? Now));

    // Drives the row through the store, so a state that carries a class or a pull request carries them as the workflow leaves them.
    private static async Task<InMemoryRunData> SeedAsync(LivemapBuildState state, long buildId = Build, InMemoryRunData? data = null)
    {
        data ??= InMemoryRunData.Create();
        var store = Store(data, Now.AddHours(-1));
        var path = state switch
        {
            LivemapBuildState.Classified or LivemapBuildState.PullRequestOpen or LivemapBuildState.PullRequestWaiting or LivemapBuildState.Merging or LivemapBuildState.Merged => LivemapBuildState.Detected,
            _ => state,
        };
        await store.RecordAsync(buildId, Gid, SteamUpdated, path, Ct);
        if (path == state)
        {
            return data;
        }

        Assert.True(await store.ClassifyAsync(buildId, LivemapClassification.Easy, 777, Ct));
        if (state == LivemapBuildState.Classified)
        {
            return data;
        }

        Assert.True(await store.OpenPullRequestAsync(buildId, Pr, Head, waiting: state == LivemapBuildState.PullRequestWaiting, Ct));
        if (state is LivemapBuildState.Merging or LivemapBuildState.Merged)
        {
            Assert.True(await store.BeginMergeAsync(buildId, Ct));
        }

        if (state == LivemapBuildState.Merged)
        {
            Assert.True(await store.MergedAsync(buildId, Ct));
        }

        return data;
    }

    private static async Task<LivemapBuild> ReadAsync(InMemoryRunData data, long buildId = Build) => (await Store(data).FindAsync(buildId, Ct))!;

    [Theory]
    [InlineData(false, LivemapBuildState.PullRequestOpen)]
    [InlineData(true, LivemapBuildState.PullRequestWaiting)]
    public async Task AClassifiedBuild_ThatGetsItsPullRequest_KeepsTheNumberAndTheHeadToJudge(bool waiting, LivemapBuildState expected)
    {
        var data = await SeedAsync(LivemapBuildState.Classified);

        var changed = await Store(data).OpenPullRequestAsync(Build, Pr, Head, waiting, Ct);

        Assert.True(changed);
        var build = await ReadAsync(data);
        Assert.Equal(expected, build.State);
        Assert.Equal(Pr, build.PullRequestNumber);
        Assert.Equal(Head, build.HeadSha);
        Assert.Equal(LivemapClassification.Easy, build.Classification);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanClassified))]
    public async Task ABuildThatIsNotClassified_CannotGetAPullRequest(LivemapBuildState state)
    {
        var data = await SeedAsync(state);
        var before = await ReadAsync(data);

        var changed = await Store(data).OpenPullRequestAsync(Build, 99, new string('b', 40), waiting: false, Ct);

        Assert.False(changed);
        var build = await ReadAsync(data);
        Assert.Equal(state, build.State);
        Assert.Equal(before.PullRequestNumber, build.PullRequestNumber);
        Assert.Equal(before.HeadSha, build.HeadSha);
    }

    [Fact]
    public async Task AWaitingBuild_ResumesIntoTheOpenState_WithItsPullRequestKept()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestWaiting);

        Assert.True(await Store(data).ResumeWaitingAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.PullRequestOpen, build.State);
        Assert.Equal(Pr, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanWaiting))]
    public async Task OnlyAWaitingBuild_CanResumeIntoTheOpenState(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).ResumeWaitingAsync(Build, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Fact]
    public async Task AnOpenBuild_BeginsTheMerge_AndFinishesItOnce()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen);

        Assert.True(await Store(data).BeginMergeAsync(Build, Ct));
        Assert.Equal(LivemapBuildState.Merging, (await ReadAsync(data)).State);
        Assert.True(await Store(data).MergedAsync(Build, Ct));
        Assert.Equal(LivemapBuildState.Merged, (await ReadAsync(data)).State);
        Assert.False(await Store(data).MergedAsync(Build, Ct));
    }

    [Fact]
    public async Task AMergeThatWasBegun_CanBeBegunAgain_SoARetriedActivityGoesOn()
    {
        var data = await SeedAsync(LivemapBuildState.Merging);

        Assert.True(await Store(data).BeginMergeAsync(Build, Ct));
        Assert.Equal(LivemapBuildState.Merging, (await ReadAsync(data)).State);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanOpenOrMerging))]
    public async Task OnlyAnOpenBuild_BeginsAMerge(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).BeginMergeAsync(Build, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanMerging))]
    public async Task ABuildNotMerging_CannotBeMarkedMerged(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).MergedAsync(Build, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Theory]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestOpen)]
    [InlineData(LivemapBuildState.Merging)]
    public async Task ABuildInThePullRequestPhase_ThatFailsAGate_NeedsTheOwner_WithItsPullRequestLeftOpen(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.True(await Store(data).FlagPullRequestAsync(Build, "Failed checks: lint / MegaLinter.", Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.NeedsOwner, build.State);
        Assert.Equal("Failed checks: lint / MegaLinter.", build.Detail);
        Assert.Equal(state == LivemapBuildState.Classified ? null : Pr, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesOutsideThePullRequestPhase))]
    public async Task ABuildOutsideThePullRequestPhase_CannotBeFlaggedByTheGate(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).FlagPullRequestAsync(Build, "late", Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Theory]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestOpen)]
    [InlineData(LivemapBuildState.Merging)]
    public async Task ABuildInThePullRequestPhase_ThatFailsForNow_IsParkedAndCountsTheAttempt(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        var next = await Store(data).ParkAsync(Build, "GitHub was unreachable", 3, TimeSpan.FromMinutes(10), Ct);

        Assert.Equal(LivemapBuildState.Parked, next);
        var build = await ReadAsync(data);
        Assert.Equal(1, build.Attempts);
        Assert.Equal(Now.AddMinutes(10), build.NextRetryAt);
    }

    [Theory]
    [InlineData(LivemapBuildState.Classified, LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestOpen, LivemapBuildState.PullRequestOpen)]
    [InlineData(LivemapBuildState.Merging, LivemapBuildState.PullRequestOpen)]
    public async Task AParkedBuild_ResumesWhereItsRecordedWorkEnded(LivemapBuildState parkedFrom, LivemapBuildState resumesAs)
    {
        var data = await SeedAsync(parkedFrom);
        await Store(data).ParkAsync(Build, "GitHub was unreachable", 3, TimeSpan.FromMinutes(10), Ct);

        Assert.True(await Store(data, Now.AddMinutes(11)).ResumeAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(resumesAs, build.State);
        Assert.Equal(parkedFrom == LivemapBuildState.Classified ? null : Pr, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayRestart))]
    public async Task ABuildWhoseBranchMovedOn_RestartsFromTheStart_WithNothingOfTheOldPullRequestLeft(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        var next = await Store(data).RestartAsync(Build, "The fork's main moved.", 3, Ct);

        Assert.Equal(LivemapBuildState.Detected, next);
        var build = await ReadAsync(data);
        Assert.Equal(1, build.Attempts);
        Assert.Equal("The fork's main moved.", build.Detail);
        Assert.Null(build.Classification);
        Assert.Null(build.CompatRunId);
        Assert.Null(build.PullRequestNumber);
        Assert.Null(build.HeadSha);
    }

    [Fact]
    public async Task ABuildThatKeepsRestarting_NeedsTheOwnerAtTheCap()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen);
        await Store(data).RestartAsync(Build, "first", 2, Ct);
        await Store(data).ClassifyAsync(Build, LivemapClassification.Known, 5, Ct);
        await Store(data).OpenPullRequestAsync(Build, 50, new string('c', 40), waiting: false, Ct);

        var next = await Store(data).RestartAsync(Build, "second", 2, Ct);

        Assert.Equal(LivemapBuildState.NeedsOwner, next);
        var build = await ReadAsync(data);
        Assert.Equal(2, build.Attempts);
        Assert.Equal(50, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayNotRestart))]
    public async Task OnlyABuildWithAnOpenOrReadyPullRequest_CanRestart(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.Null(await Store(data).RestartAsync(Build, "late", 3, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayReopen))]
    public async Task ABuildWhoseMainMovedOn_GoesBackToClassified_KeepingWhatTheCompatRunDecided(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        var next = await Store(data).ReopenAsync(Build, "Main moved on.", 3, Ct);

        Assert.Equal(LivemapBuildState.Classified, next);
        var build = await ReadAsync(data);
        Assert.Equal(1, build.Attempts);
        Assert.Equal("Main moved on.", build.Detail);
        Assert.Equal(LivemapClassification.Easy, build.Classification);
        Assert.Equal(777, build.CompatRunId);
        Assert.Null(build.PullRequestNumber);
        Assert.Null(build.HeadSha);
    }

    [Fact]
    public async Task ABuildThatKeepsReopening_NeedsTheOwnerAtTheCap_WithItsPullRequestKept()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen);
        await Store(data).ReopenAsync(Build, "first", 2, Ct);
        await Store(data).OpenPullRequestAsync(Build, 50, new string('c', 40), waiting: false, Ct);

        var next = await Store(data).ReopenAsync(Build, "second", 2, Ct);

        Assert.Equal(LivemapBuildState.NeedsOwner, next);
        var build = await ReadAsync(data);
        Assert.Equal(2, build.Attempts);
        Assert.Equal(50, build.PullRequestNumber);
    }

    [Theory]
    [MemberData(nameof(StatesThatMayNotReopen))]
    public async Task OnlyABuildWithAnOpenPullRequestBeingJudged_CanReopen(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.Null(await Store(data).ReopenAsync(Build, "late", 3, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Fact]
    public async Task AnOpenBuild_CanBeHeldAsWaiting_WithItsPullRequestKept()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen);

        Assert.True(await Store(data).HoldAsync(Build, Ct));

        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.PullRequestWaiting, build.State);
        Assert.Equal(Pr, build.PullRequestNumber);
        Assert.Equal(Head, build.HeadSha);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanOpen))]
    public async Task OnlyAnOpenBuild_CanBeHeld(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).HoldAsync(Build, Ct));
        Assert.Equal(state, (await ReadAsync(data)).State);
    }

    [Fact]
    public async Task ANewerBuild_SupersedesAnOlderOnesWithAnOpenPullRequest_ButNotOneThatIsMerging()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen, 100);
        await SeedAsync(LivemapBuildState.PullRequestWaiting, 200, data);
        await SeedAsync(LivemapBuildState.Merging, 300, data);
        await SeedAsync(LivemapBuildState.Merged, 400, data);

        var count = await Store(data).SupersedeOlderAsync(500, Ct);

        Assert.Equal(2, count);
        Assert.Equal(LivemapBuildState.Superseded, (await ReadAsync(data, 100)).State);
        Assert.Equal(LivemapBuildState.Superseded, (await ReadAsync(data, 200)).State);
        Assert.Equal(LivemapBuildState.Merging, (await ReadAsync(data, 300)).State);
        Assert.Equal(LivemapBuildState.Merged, (await ReadAsync(data, 400)).State);
    }

    [Fact]
    public async Task ASupersededBuild_WithAPullRequest_IsListedUntilItsPullRequestIsReleased()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen, 100);
        await SeedAsync(LivemapBuildState.Classified, 200, data);
        await Store(data).SupersedeOlderAsync(300, Ct);

        var listed = await Store(data).FindSupersededWithPullRequestAsync(Ct);

        var only = Assert.Single(listed);
        Assert.Equal(100, only.BuildId);
        Assert.Equal(Pr, only.PullRequestNumber);
        Assert.True(await Store(data).ReleasePullRequestAsync(100, Ct));
        Assert.Empty(await Store(data).FindSupersededWithPullRequestAsync(Ct));
        Assert.Null((await ReadAsync(data, 100)).PullRequestNumber);
        Assert.Equal(LivemapBuildState.Superseded, (await ReadAsync(data, 100)).State);
    }

    [Fact]
    public async Task ABuildThatIsNotSuperseded_KeepsItsPullRequest()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestOpen);

        Assert.False(await Store(data).ReleasePullRequestAsync(Build, Ct));
        Assert.Equal(Pr, (await ReadAsync(data)).PullRequestNumber);
    }

    [Fact]
    public async Task TheBuildInFlight_IncludesTheOnesWithWorkLeftOnTheirPullRequest()
    {
        var data = await SeedAsync(LivemapBuildState.Classified, 100);
        await SeedAsync(LivemapBuildState.PullRequestOpen, 200, data);
        await SeedAsync(LivemapBuildState.Merging, 300, data);
        await SeedAsync(LivemapBuildState.PullRequestWaiting, 400, data);
        await SeedAsync(LivemapBuildState.Merged, 500, data);

        Assert.Equal(300, (await Store(data).FindActiveAsync(Ct))!.BuildId);
    }

    [Fact]
    public async Task AWaitingBuild_IsNotInFlight()
    {
        var data = await SeedAsync(LivemapBuildState.PullRequestWaiting);

        Assert.Null(await Store(data).FindActiveAsync(Ct));
    }

    [Fact]
    public async Task APullRequestTransitionThatLosesARace_ChangesNothing()
    {
        var data = await SeedAsync(LivemapBuildState.Classified);
        var store = Store(data);
        var results = new List<bool>();
        foreach (var move in new Func<Task<bool>>[]
        {
            () => store.OpenPullRequestAsync(Build, Pr, Head, waiting: false, Ct),
            () => store.FlagPullRequestAsync(Build, "late", Ct),
            async () => await store.RestartAsync(Build, "late", 3, Ct) is not null,
            async () => await store.ReopenAsync(Build, "late", 3, Ct) is not null,
            () => store.HoldAsync(Build, Ct),
            async () => await store.ParkAsync(Build, "late", 3, TimeSpan.FromMinutes(1), Ct) is not null,
        })
        {
            data.FailNextSaveWith(new DbUpdateConcurrencyException("moved"));
            results.Add(await move());
        }

        Assert.All(results, Assert.False);
        var build = await ReadAsync(data);
        Assert.Equal(LivemapBuildState.Classified, build.State);
        Assert.Null(build.PullRequestNumber);
        Assert.Equal(0, build.Attempts);
    }
}
