using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildStoreHardTests
{
    private const string Gid = "8886672487109033979";

    private const long Build = 26430253;

    private const long CompatRun = 987654321;

    private const string BaseSha = "0123456789abcdef0123456789abcdef01234567";

    private const string Stored = "{\"status\":\"data\"}";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<LivemapBuildState> StatesOtherThanPatching => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Patching));

    public static TheoryData<LivemapBuildState> StatesOtherThanDetected => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Detected));

    public static TheoryData<LivemapBuildState> StatesOtherThanClassified => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Classified));

    private static LivemapBuildStore Store(InMemoryRunData data, DateTimeOffset? at = null) => new(data.Factory, new FixedTimeProvider(at ?? Now));

    private static async Task<InMemoryRunData> SeedAsync(LivemapBuildState state = LivemapBuildState.Detected, long buildId = Build, InMemoryRunData? data = null, Action<LivemapBuild>? columns = null)
    {
        data ??= InMemoryRunData.Create();
        await Store(data, Now.AddHours(-1)).RecordAsync(buildId, Gid, SteamUpdated, state, Ct);
        if (columns is not null)
        {
            await using var db = await data.Factory.CreateDbContextAsync(Ct);
            columns(await db.LivemapBuilds.SingleAsync(b => b.BuildId == buildId, Ct));
            await db.SaveChangesAsync(Ct);
        }

        return data;
    }

    private static Task<InMemoryRunData> SeedPatchingAsync(Action<LivemapBuild>? columns = null, long buildId = Build, InMemoryRunData? data = null) =>
        SeedAsync(LivemapBuildState.Patching, buildId, data, build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.CompatRunId = CompatRun;
            columns?.Invoke(build);
        });

    [Fact]
    public void TheNewStates_FitTheirColumn()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost").Options;
        using var db = new RunDataDbContext(options);
        var build = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(LivemapBuild))!;

        Assert.All(new[] { LivemapBuildState.Patching.ToString(), LivemapBuildState.OwnerReview.ToString(), LivemapClassification.Hard.ToString() }, name => Assert.InRange(name.Length, 1, 24));
        Assert.Equal(40, build.FindProperty(nameof(LivemapBuild.PatchBaseSha))!.GetMaxLength());
        Assert.Null(build.FindProperty(nameof(LivemapBuild.PatchResult))!.GetMaxLength());
        Assert.Null(build.FindProperty(nameof(LivemapBuild.PatchNotes))!.GetMaxLength());
    }

    [Fact]
    public async Task ADetectedBuild_ThatIsHard_StartsPatchingAgainstItsCompatRun()
    {
        var data = await SeedAsync();

        var changed = await Store(data).BeginPatchAsync(Build, CompatRun, Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Patching, build!.State);
        Assert.Equal(LivemapClassification.Hard, build.Classification);
        Assert.Equal(CompatRun, build.CompatRunId);
        Assert.Equal(0, build.AgentRuns);
        Assert.Equal(Now, build.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanDetected))]
    public async Task ABuildThatIsNotDetected_CannotStartPatching(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).BeginPatchAsync(Build, CompatRun, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.Classification);
    }

    [Fact]
    public async Task AnAgentRun_IsCountedOnceEvenWhenItsActivityIsRetried()
    {
        var data = await SeedPatchingAsync();

        var first = await Store(data).BeginAgentRunAsync(Build, 1, Ct);
        var retried = await Store(data).BeginAgentRunAsync(Build, 1, Ct);
        var second = await Store(data).BeginAgentRunAsync(Build, 2, Ct);

        Assert.True(first);
        Assert.True(retried);
        Assert.True(second);
        Assert.Equal(2, (await Store(data).FindAsync(Build, Ct))!.AgentRuns);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 4)]
    public async Task AnAgentRun_IsRefused_WhenItIsNotTheNextOrTheCurrentOne(int recorded, int run)
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = recorded);

        Assert.False(await Store(data).BeginAgentRunAsync(Build, run, Ct));

        Assert.Equal(recorded, (await Store(data).FindAsync(Build, Ct))!.AgentRuns);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanPatching))]
    public async Task ABuildThatIsNotPatching_CannotBeginAnAgentRun(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).BeginAgentRunAsync(Build, 1, Ct));

        Assert.Equal(0, (await Store(data).FindAsync(Build, Ct))!.AgentRuns);
    }

    [Fact]
    public async Task APartialResult_KeepsItsNotesForTheNextRun_AndStaysPatching()
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = 1);

        var changed = await Store(data).SavePartialAsync(Build, "the sigset needs one more site", Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Patching, build!.State);
        Assert.Equal("the sigset needs one more site", build.PatchNotes);
        Assert.Equal(1, build.AgentRuns);
        Assert.Equal(1, build.Version);
    }

    [Fact]
    public async Task TheNotesOfAPartialResult_AreCappedToTheLengthTheParserAllows()
    {
        var data = await SeedPatchingAsync();

        await Store(data).SavePartialAsync(Build, new string('n', LivemapResult.MaxNotesLength * 2), Ct);

        Assert.Equal(LivemapResult.MaxNotesLength, (await Store(data).FindAsync(Build, Ct))!.PatchNotes!.Length);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanPatching))]
    public async Task ABuildThatIsNotPatching_KeepsNoPartialNotes(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).SavePartialAsync(Build, "late", Ct));

        Assert.Null((await Store(data).FindAsync(Build, Ct))!.PatchNotes);
    }

    [Fact]
    public async Task AValidatedResult_IsStoredWithTheMainItWasMadeAgainst_AndTheBuildIsClassified()
    {
        var data = await SeedPatchingAsync(build =>
        {
            build.AgentRuns = 2;
            build.PatchNotes = "older notes";
        });

        var changed = await Store(data).StorePatchAsync(Build, Stored, BaseSha, Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Classified, build!.State);
        Assert.Equal(LivemapClassification.Hard, build.Classification);
        Assert.Equal(Stored, build.PatchResult);
        Assert.Equal(BaseSha, build.PatchBaseSha);
        Assert.Null(build.PatchNotes);
        Assert.Equal(2, build.AgentRuns);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanPatching))]
    public async Task ABuildThatIsNotPatching_CannotStoreAResult(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).StorePatchAsync(Build, Stored, BaseSha, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.PatchResult);
    }

    [Fact]
    public async Task AUsageLimit_ParksThePatchUntilTheReset_WithoutCountingAnAttempt()
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = 2);
        var reset = Now.AddHours(5);

        var changed = await Store(data).ParkUntilAsync(Build, "The usage limit was reached.", reset, refundRun: true, Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Parked, build!.State);
        Assert.Equal(reset, build.NextRetryAt);
        Assert.Equal(0, build.Attempts);
        Assert.Equal(1, build.AgentRuns);
        Assert.Equal("The usage limit was reached.", build.Detail);
    }

    [Fact]
    public async Task AParkThatRefundsNoRun_KeepsTheCount()
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = 2);

        await Store(data).ParkUntilAsync(Build, "daily cap", Now.AddHours(1), refundRun: false, Ct);

        Assert.Equal(2, (await Store(data).FindAsync(Build, Ct))!.AgentRuns);
    }

    [Fact]
    public async Task AParkThatRefundsARun_NeverGoesBelowZero()
    {
        var data = await SeedPatchingAsync();

        await Store(data).ParkUntilAsync(Build, "limit", Now.AddHours(1), refundRun: true, Ct);

        Assert.Equal(0, (await Store(data).FindAsync(Build, Ct))!.AgentRuns);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanPatching))]
    public async Task ABuildThatIsNotPatching_CannotBeParkedUntilAReset(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).ParkUntilAsync(Build, "late", Now.AddHours(1), refundRun: false, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.NextRetryAt);
    }

    [Fact]
    public async Task APatchingBuild_ThatNoRunCanFinish_NeedsAPerson()
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = 3);

        var changed = await Store(data).NeedsPersonAsync(Build, "Three agent runs left it undone: needs a person.", Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.NeedsOwner, build!.State);
        Assert.Equal("Three agent runs left it undone: needs a person.", build.Detail);
        Assert.Null(build.NotifiedAt);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanPatching))]
    public async Task ABuildThatIsNotPatching_CannotBeHandedToAPerson(LivemapBuildState state)
    {
        var data = await SeedAsync(state);

        Assert.False(await Store(data).NeedsPersonAsync(Build, "late", Ct));

        Assert.Equal(state, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task APatchingBuild_ThatTimesOutInTheQueue_IsParkedAsAnyOtherBuildIs()
    {
        var data = await SeedPatchingAsync();

        var state = await Store(data).ParkAsync(Build, "The agent pod did not start.", 3, TimeSpan.FromMinutes(30), Ct);

        Assert.Equal(LivemapBuildState.Parked, state);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(1, build!.Attempts);
        Assert.Equal(Now.AddMinutes(30), build.NextRetryAt);
    }

    [Fact]
    public async Task AParkedPatch_ResumesPatching_WhileItHasNoResult()
    {
        var data = await SeedPatchingAsync(build => build.AgentRuns = 1);
        await Store(data).ParkAsync(Build, "queue", 3, TimeSpan.FromMinutes(10), Ct);

        Assert.True(await Store(data, Now.AddMinutes(10)).ResumeAsync(Build, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Patching, build!.State);
        Assert.Equal(1, build.AgentRuns);
    }

    [Fact]
    public async Task AParkedHardBuild_WithAStoredResult_ResumesFromTheClassifiedStep()
    {
        var data = await SeedAsync(LivemapBuildState.Parked, columns: build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.PatchResult = Stored;
            build.PatchBaseSha = BaseSha;
            build.NextRetryAt = Now;
        });

        Assert.True(await Store(data, Now).ResumeAsync(Build, Ct));

        Assert.Equal(LivemapBuildState.Classified, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task AParkedHardBuild_WithAPullRequest_ResumesFromThePullRequest()
    {
        var data = await SeedAsync(LivemapBuildState.Parked, columns: build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.PatchResult = Stored;
            build.PullRequestNumber = 12;
            build.NextRetryAt = Now;
        });

        Assert.True(await Store(data, Now).ResumeAsync(Build, Ct));

        Assert.Equal(LivemapBuildState.PullRequestOpen, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task ADataResultOnAClassifiedBuild_OpensItsPullRequestAsAnyOtherDoes()
    {
        var data = await SeedPatchingAsync();
        await Store(data).StorePatchAsync(Build, Stored, BaseSha, Ct);

        Assert.True(await Store(data).OpenPullRequestAsync(Build, 12, BaseSha, waiting: false, Ct));

        Assert.Equal(LivemapBuildState.PullRequestOpen, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task ACodePullRequest_WaitsForTheOwner_AndKeepsItsNumberAndHead()
    {
        var data = await SeedPatchingAsync();
        await Store(data).StorePatchAsync(Build, Stored, BaseSha, Ct);

        var changed = await Store(data).OpenOwnerReviewAsync(Build, 12, "fedcba9876543210fedcba9876543210fedcba98", Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.OwnerReview, build!.State);
        Assert.Equal(12, build.PullRequestNumber);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba98", build.HeadSha);
        Assert.Null(build.NotifiedAt);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanClassified))]
    public async Task OnlyAClassifiedBuild_CanWaitForTheOwnersReview(LivemapBuildState state)
    {
        var data = await SeedAsync(state, columns: build => build.Classification = LivemapClassification.Hard);

        Assert.False(await Store(data).OpenOwnerReviewAsync(Build, 12, BaseSha, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.PullRequestNumber);
    }

    [Theory]
    [InlineData(LivemapClassification.Known)]
    [InlineData(LivemapClassification.Easy)]
    public async Task ABuildThatIsNotHard_NeverWaitsForTheOwnersReview(LivemapClassification classification)
    {
        var data = await SeedAsync(LivemapBuildState.Classified, columns: build => build.Classification = classification);

        Assert.False(await Store(data).OpenOwnerReviewAsync(Build, 12, BaseSha, Ct));

        Assert.Equal(LivemapBuildState.Classified, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task TheOwnerIsToldOnceOfACodePullRequest_AndTheBuildStaysInReview()
    {
        var data = await SeedAsync(LivemapBuildState.OwnerReview);

        Assert.True(await Store(data, Now.AddMinutes(1)).NotifiedAsync(Build, Ct));
        Assert.False(await Store(data, Now.AddMinutes(2)).NotifiedAsync(Build, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.OwnerReview, build!.State);
        Assert.Equal(Now.AddMinutes(1), build.NotifiedAt);
    }

    [Fact]
    public async Task ACodePullRequestNobodyWasToldOf_IsFoundAmongTheUnnotified()
    {
        var data = await SeedAsync(LivemapBuildState.OwnerReview, 100);
        await SeedAsync(LivemapBuildState.OwnerReview, 200, data, build => build.NotifiedAt = Now);
        await SeedAsync(LivemapBuildState.NeedsOwner, 300, data);

        var found = await Store(data).FindUnnotifiedAsync(Ct);

        Assert.Equal([100L, 300L], found.Select(b => b.BuildId));
    }

    [Fact]
    public async Task APatchInFlight_IsTheBuildInFlight_AndAReviewIsNot()
    {
        var data = await SeedAsync(LivemapBuildState.OwnerReview, 300);
        await SeedPatchingAsync(buildId: 200, data: data);

        var active = await Store(data).FindActiveAsync(Ct);

        Assert.Equal(200, active!.BuildId);
    }

    [Fact]
    public async Task ANewerBuild_SupersedesAnOlderPatch_ButNotACodePullRequestWaitingForTheOwner()
    {
        var data = await SeedPatchingAsync(buildId: 100);
        await SeedAsync(LivemapBuildState.OwnerReview, 200, data);
        await SeedAsync(LivemapBuildState.Parked, 300, data, build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.NextRetryAt = Now;
        });
        await SeedAsync(data: data, buildId: 400);

        var count = await Store(data).SupersedeOlderAsync(400, Ct);

        Assert.Equal(2, count);
        Assert.Equal(LivemapBuildState.Superseded, (await Store(data).FindAsync(100, Ct))!.State);
        Assert.Equal(LivemapBuildState.OwnerReview, (await Store(data).FindAsync(200, Ct))!.State);
        Assert.Equal(LivemapBuildState.Superseded, (await Store(data).FindAsync(300, Ct))!.State);
    }

    [Fact]
    public async Task ABuildThatStartsOver_DropsTheStoredResult_ButKeepsTheRunsAlreadySpent()
    {
        var data = await SeedAsync(LivemapBuildState.Classified, columns: build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.CompatRunId = CompatRun;
            build.AgentRuns = 2;
            build.PatchResult = Stored;
            build.PatchBaseSha = BaseSha;
            build.PatchNotes = "stale";
        });

        var state = await Store(data).RestartAsync(Build, "Main changed.", 3, Ct);

        Assert.Equal(LivemapBuildState.Detected, state);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Null(build!.PatchResult);
        Assert.Null(build.PatchBaseSha);
        Assert.Null(build.PatchNotes);
        Assert.Null(build.Classification);
        Assert.Equal(2, build.AgentRuns);
    }

    [Fact]
    public async Task ASupersededBuild_ReleasesItsStoredResult()
    {
        var data = await SeedAsync(LivemapBuildState.Superseded, columns: build =>
        {
            build.Classification = LivemapClassification.Hard;
            build.PatchResult = Stored;
            build.PatchBaseSha = BaseSha;
        });

        Assert.True(await Store(data).ReleasePullRequestAsync(Build, Ct));

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Null(build!.PatchResult);
        Assert.Null(build.PatchBaseSha);
        Assert.Null(build.Classification);
    }

    [Fact]
    public async Task AHardTransitionThatLosesARace_ChangesNothing()
    {
        var data = await SeedPatchingAsync();
        var store = Store(data);
        var results = new List<bool>();
        foreach (var move in new Func<Task<bool>>[]
        {
            () => store.BeginAgentRunAsync(Build, 1, Ct),
            () => store.SavePartialAsync(Build, "late", Ct),
            () => store.StorePatchAsync(Build, Stored, BaseSha, Ct),
            () => store.ParkUntilAsync(Build, "late", Now.AddHours(1), refundRun: false, Ct),
            () => store.NeedsPersonAsync(Build, "late", Ct),
        })
        {
            data.FailNextSaveWith(new DbUpdateConcurrencyException("moved"));
            results.Add(await move());
        }

        Assert.All(results, Assert.False);
        var build = await store.FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Patching, build!.State);
        Assert.Equal(0, build.AgentRuns);
        Assert.Null(build.PatchResult);
        Assert.Null(build.PatchNotes);
    }
}
