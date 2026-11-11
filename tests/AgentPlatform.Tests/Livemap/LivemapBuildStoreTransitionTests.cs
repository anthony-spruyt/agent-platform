using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildStoreTransitionTests
{
    private const string Gid = "8886672487109033979";

    private const long Build = 26430253;

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<LivemapBuildState> StatesOtherThanDetected => new(Enum.GetValues<LivemapBuildState>().Where(s => s != LivemapBuildState.Detected));

    public static TheoryData<LivemapBuildState> StatesWithNoWorkflowAtWork => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.Detected or LivemapBuildState.Patching or LivemapBuildState.Classified or LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging or LivemapBuildState.Merged or LivemapBuildState.Released or LivemapBuildState.Verified or LivemapBuildState.PinOpen or LivemapBuildState.Pinned)));

    public static TheoryData<LivemapBuildState> StatesNeitherDetectedNorParked => new(Enum.GetValues<LivemapBuildState>().Where(s => s is not (LivemapBuildState.Detected or LivemapBuildState.Parked)));

    private static LivemapBuildStore Store(InMemoryRunData data, DateTimeOffset? at = null) => new(data.Factory, new FixedTimeProvider(at ?? Now));

    private static async Task<InMemoryRunData> SeedAsync(long buildId = Build, LivemapBuildState state = LivemapBuildState.Detected, InMemoryRunData? data = null)
    {
        data ??= InMemoryRunData.Create();
        await Store(data, Now.AddHours(-1)).RecordAsync(buildId, Gid, SteamUpdated, state, Ct);
        return data;
    }

    [Fact]
    public void EveryStateAndClassName_FitsItsColumn()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost").Options;
        using var db = new RunDataDbContext(options);
        var build = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(LivemapBuild))!;

        Assert.All(Enum.GetNames<LivemapBuildState>(), name => Assert.InRange(name.Length, 1, build.FindProperty(nameof(LivemapBuild.State))!.GetMaxLength()!.Value));
        Assert.All(Enum.GetNames<LivemapClassification>(), name => Assert.InRange(name.Length, 1, build.FindProperty(nameof(LivemapBuild.Classification))!.GetMaxLength()!.Value));
    }

    [Fact]
    public async Task ADetectedBuild_ThatIsClassified_KeepsTheClassAndTheRunToReadAgain()
    {
        var data = await SeedAsync();

        var changed = await Store(data).ClassifyAsync(Build, LivemapClassification.Easy, 987654321, Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Classified, build!.State);
        Assert.Equal(LivemapClassification.Easy, build.Classification);
        Assert.Equal(987654321, build.CompatRunId);
        Assert.Equal(Now, build.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanDetected))]
    public async Task ABuildThatIsNotDetected_CannotBeClassified(LivemapBuildState state)
    {
        var data = await SeedAsync(state: state);

        var changed = await Store(data).ClassifyAsync(Build, LivemapClassification.Known, 1, Ct);

        Assert.False(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.Classification);
    }

    [Fact]
    public async Task ABuildThatWasNeverRecorded_CannotBeClassified()
    {
        Assert.False(await Store(InMemoryRunData.Create()).ClassifyAsync(Build, LivemapClassification.Known, 1, Ct));
    }

    [Fact]
    public async Task ADetectedBuild_FlaggedForTheOwner_KeepsTheReason()
    {
        var data = await SeedAsync();

        var changed = await Store(data).FlagForOwnerAsync(Build, "the patch is hard", Ct);

        Assert.True(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.NeedsOwner, build!.State);
        Assert.Equal("the patch is hard", build.Detail);
        Assert.Equal(Now, build.UpdatedAt);
    }

    [Fact]
    public async Task AReasonThatIsTooLong_IsCutToTheColumn()
    {
        var data = await SeedAsync();

        await Store(data).FlagForOwnerAsync(Build, new string('x', LivemapBuildStore.MaxDetailChars * 3), Ct);

        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildStore.MaxDetailChars, build!.Detail!.Length);
    }

    [Theory]
    [MemberData(nameof(StatesOtherThanDetected))]
    public async Task ABuildThatIsNotDetected_CannotBeFlaggedForTheOwner(LivemapBuildState state)
    {
        var data = await SeedAsync(state: state);

        var changed = await Store(data).FlagForOwnerAsync(Build, "late", Ct);

        Assert.False(changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Null(build.Detail);
    }

    [Fact]
    public async Task ADetectedBuild_ThatFailsForNow_IsParkedUntilItsRetryTime()
    {
        var data = await SeedAsync();

        var state = await Store(data).ParkAsync(Build, "the runner was busy", 3, TimeSpan.FromMinutes(10), Ct);

        Assert.Equal(LivemapBuildState.Parked, state);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Parked, build!.State);
        Assert.Equal(1, build.Attempts);
        Assert.Equal(Now.AddMinutes(10), build.NextRetryAt);
        Assert.Equal("the runner was busy", build.Detail);
    }

    [Fact]
    public async Task ABuildThatKeepsFailing_NeedsTheOwnerAtTheCap()
    {
        var data = await SeedAsync();
        var at = Now;
        var results = new List<LivemapBuildState?>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            results.Add(await Store(data, at).ParkAsync(Build, $"try {attempt}", 3, TimeSpan.FromMinutes(10), Ct));
            at = at.AddMinutes(11);
            await Store(data, at).ResumeAsync(Build, Ct);
        }

        Assert.Equal([LivemapBuildState.Parked, LivemapBuildState.Parked, LivemapBuildState.NeedsOwner], results);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.NeedsOwner, build!.State);
        Assert.Equal(3, build.Attempts);
        Assert.Equal("try 2", build.Detail);
    }

    [Theory]
    [MemberData(nameof(StatesWithNoWorkflowAtWork))]
    public async Task ABuildWithNoWorkflowAtWork_CannotBeParked(LivemapBuildState state)
    {
        var data = await SeedAsync(state: state);

        var parked = await Store(data).ParkAsync(Build, "late", 3, TimeSpan.FromMinutes(10), Ct);

        Assert.Null(parked);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(state, build!.State);
        Assert.Equal(0, build.Attempts);
    }

    [Fact]
    public async Task ABuildThatWasNeverRecorded_CannotBeParked()
    {
        Assert.Null(await Store(InMemoryRunData.Create()).ParkAsync(Build, "late", 3, TimeSpan.FromMinutes(10), Ct));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(9, false)]
    [InlineData(11, true)]
    public async Task AParkedBuild_ResumesOnlyOnceItsRetryTimeHasCome(int minutesLater, bool resumes)
    {
        var data = await SeedAsync();
        await Store(data).ParkAsync(Build, "the runner was busy", 3, TimeSpan.FromMinutes(10), Ct);

        var changed = await Store(data, Now.AddMinutes(minutesLater)).ResumeAsync(Build, Ct);

        Assert.Equal(resumes, changed);
        var build = await Store(data).FindAsync(Build, Ct);
        Assert.Equal(resumes ? LivemapBuildState.Detected : LivemapBuildState.Parked, build!.State);
        Assert.Equal(1, build.Attempts);
    }

    [Theory]
    [MemberData(nameof(StatesNeitherDetectedNorParked))]
    public async Task OnlyAParkedBuild_CanResume(LivemapBuildState state)
    {
        var data = await SeedAsync(state: state);

        var changed = await Store(data, Now.AddDays(1)).ResumeAsync(Build, Ct);

        Assert.False(changed);
        Assert.Equal(state, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task ADetectedBuild_CannotResume()
    {
        var data = await SeedAsync();

        Assert.False(await Store(data, Now.AddDays(1)).ResumeAsync(Build, Ct));
    }

    [Fact]
    public async Task ABuildThatIsParkedWithoutARetryTime_CannotResume()
    {
        var data = await SeedAsync(state: LivemapBuildState.Parked);

        Assert.False(await Store(data, Now.AddDays(1)).ResumeAsync(Build, Ct));
    }

    [Fact]
    public async Task ANewerBuild_SupersedesTheOlderOnesInFlight_AndOnlyThose()
    {
        var data = await SeedAsync(100);
        await SeedAsync(200, LivemapBuildState.Parked, data);
        await SeedAsync(300, LivemapBuildState.Classified, data);
        await SeedAsync(400, LivemapBuildState.Done, data);
        await SeedAsync(500, LivemapBuildState.NeedsOwner, data);
        await SeedAsync(600, LivemapBuildState.Superseded, data);
        await SeedAsync(700, data: data);
        await SeedAsync(800, data: data);

        var count = await Store(data).SupersedeOlderAsync(700, Ct);

        Assert.Equal(3, count);
        Assert.Equal(1, (await Store(data).FindAsync(100, Ct))!.Version);
        Assert.Equal(Now, (await Store(data).FindAsync(100, Ct))!.UpdatedAt);
        var states = new Dictionary<long, LivemapBuildState>();
        foreach (var id in new long[] { 100, 200, 300, 400, 500, 600, 700, 800 })
        {
            states[id] = (await Store(data).FindAsync(id, Ct))!.State;
        }

        Assert.Equal(LivemapBuildState.Superseded, states[100]);
        Assert.Equal(LivemapBuildState.Superseded, states[200]);
        Assert.Equal(LivemapBuildState.Superseded, states[300]);
        Assert.Equal(LivemapBuildState.Done, states[400]);
        Assert.Equal(LivemapBuildState.NeedsOwner, states[500]);
        Assert.Equal(LivemapBuildState.Superseded, states[600]);
        Assert.Equal(LivemapBuildState.Detected, states[700]);
        Assert.Equal(LivemapBuildState.Detected, states[800]);
    }

    [Fact]
    public async Task ASupersedeThatLosesARace_IsTriedOnceMore()
    {
        var data = await SeedAsync(100);
        data.FailNextSaveWith(new DbUpdateConcurrencyException("moved"));

        var count = await Store(data).SupersedeOlderAsync(200, Ct);

        Assert.Equal(1, count);
        Assert.Equal(LivemapBuildState.Superseded, (await Store(data).FindAsync(100, Ct))!.State);
    }

    [Fact]
    public async Task ASupersedeThatKeepsLosing_IsReported()
    {
        var data = await SeedAsync(100);
        data.FailSavesWith(new DbUpdateConcurrencyException("moved"));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Store(data).SupersedeOlderAsync(200, Ct));
    }

    [Fact]
    public async Task ATransitionThatLosesARace_ChangesNothing()
    {
        var data = await SeedAsync();
        var store = Store(data);
        var results = new List<bool>();
        foreach (var move in new Func<Task<bool>>[]
        {
            () => store.ClassifyAsync(Build, LivemapClassification.Known, 1, Ct),
            () => store.FlagForOwnerAsync(Build, "late", Ct),
            async () => await store.ParkAsync(Build, "late", 3, TimeSpan.FromMinutes(1), Ct) is not null,
        })
        {
            data.FailNextSaveWith(new DbUpdateConcurrencyException("moved"));
            results.Add(await move());
        }

        Assert.All(results, Assert.False);
        var build = await store.FindAsync(Build, Ct);
        Assert.Equal(LivemapBuildState.Detected, build!.State);
        Assert.Equal(0, build.Attempts);
        Assert.Null(build.Detail);
    }

    [Fact]
    public async Task AResumeThatLosesARace_ChangesNothing()
    {
        var data = await SeedAsync();
        await Store(data).ParkAsync(Build, "busy", 3, TimeSpan.FromMinutes(1), Ct);
        data.FailNextSaveWith(new DbUpdateConcurrencyException("moved"));

        Assert.False(await Store(data, Now.AddHours(1)).ResumeAsync(Build, Ct));
        Assert.Equal(LivemapBuildState.Parked, (await Store(data).FindAsync(Build, Ct))!.State);
    }

    [Fact]
    public async Task EveryTransition_BumpsTheVersionOfTheRow()
    {
        var data = await SeedAsync();
        await Store(data).ParkAsync(Build, "busy", 3, TimeSpan.FromMinutes(1), Ct);
        await Store(data, Now.AddHours(1)).ResumeAsync(Build, Ct);
        await Store(data).ClassifyAsync(Build, LivemapClassification.Known, 1, Ct);

        Assert.Equal(3, (await Store(data).FindAsync(Build, Ct))!.Version);
    }

    [Fact]
    public async Task TheBuildInFlight_IsTheNewestDetectedOne()
    {
        var data = await SeedAsync(100);
        await SeedAsync(300, data: data);
        await SeedAsync(200, data: data);
        await SeedAsync(400, LivemapBuildState.Parked, data);
        await SeedAsync(500, LivemapBuildState.Done, data);

        var active = await Store(data).FindActiveAsync(Ct);

        Assert.Equal(300, active!.BuildId);
    }

    [Fact]
    public async Task WithNoBuildAtWork_NothingIsInFlight()
    {
        var data = await SeedAsync(100, LivemapBuildState.Parked);
        await SeedAsync(200, LivemapBuildState.NeedsOwner, data);

        Assert.Null(await Store(data).FindActiveAsync(Ct));
    }
}
