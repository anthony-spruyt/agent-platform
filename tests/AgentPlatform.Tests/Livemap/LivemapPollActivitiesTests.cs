using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPollActivitiesTests
{
    private const string Gid = "8886672487109033979";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class FakeSteam : ISteamAppInfo
    {
        public SteamBuild Build { get; set; } = new(26430253, Gid, SteamUpdated);

        private int _failOnlyFirstCalls;

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public void FailFirst(int calls, Exception failure)
        {
            _failOnlyFirstCalls = calls;
            Failure = failure;
        }

        public Task<SteamBuild> GetPublicBuildAsync(CancellationToken cancellationToken)
        {
            Calls++;
            var failing = Failure is not null && (_failOnlyFirstCalls == 0 || Calls <= _failOnlyFirstCalls);
            return failing ? Task.FromException<SteamBuild>(Failure!) : Task.FromResult(Build);
        }
    }

    internal sealed class FakeShipped : IShippedBuilds
    {
        public HashSet<long> Builds { get; } = [];

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlySet<long>> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null ? Task.FromResult<IReadOnlySet<long>>(Builds) : Task.FromException<IReadOnlySet<long>>(Failure);
        }
    }

    internal sealed class FakeStarter : ILivemapBuildStarter
    {
        public List<(long BuildId, string ManifestGid)> Started { get; } = [];

        public Exception? Failure { get; set; }

        public Task StartAsync(long buildId, string manifestGid, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Started.Add((buildId, manifestGid));
            return Task.CompletedTask;
        }
    }

    internal sealed class Rig
    {
        public Rig(bool enabled = true)
        {
            Data = InMemoryRunData.Create();
            Store = new LivemapBuildStore(Data.Factory, new FixedTimeProvider(Now));
            Config = new LivemapOptions { Enabled = enabled };
            Activities = new LivemapPollActivities(
                Options.Create(Config),
                Steam,
                Shipped,
                Store,
                Starter,
                PullRequests,
                NullLogger<LivemapPollActivities>.Instance);
        }

        public LivemapOptions Config { get; }

        public FakeLivemapPullRequests PullRequests { get; } = new();

        public FakeStarter Starter { get; } = new();

        public InMemoryRunData Data { get; }

        public LivemapBuildStore Store { get; }

        public FakeSteam Steam { get; } = new();

        public FakeShipped Shipped { get; } = new();

        public LivemapPollActivities Activities { get; }

        public Task<LivemapPollResult> PollAsync() => new ActivityEnvironment().RunAsync(Activities.PollAsync);

        public async Task<int> RowCountAsync()
        {
            await using var db = await Data.Factory.CreateDbContextAsync(Ct);
            return await db.LivemapBuilds.CountAsync(Ct);
        }
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_NothingIsRead_AndNothingIsRecorded()
    {
        var rig = new Rig(enabled: false);

        var result = await rig.PollAsync();

        Assert.Equal(new LivemapPollResult(LivemapPollOutcome.Disabled, null), result);
        Assert.Equal(0, rig.Steam.Calls);
        Assert.Equal(0, rig.Shipped.Calls);
        Assert.Equal(0, await rig.RowCountAsync());
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task ANewBuildTheReleaseDoesNotCover_IsRecordedAsDetected()
    {
        var rig = new Rig();
        rig.Shipped.Builds.Add(100);

        var result = await rig.PollAsync();

        Assert.Equal(new LivemapPollResult(LivemapPollOutcome.Detected, 26430253), result);
        var row = await rig.Store.FindAsync(26430253, Ct);
        Assert.NotNull(row);
        Assert.Equal(LivemapBuildState.Detected, row.State);
        Assert.Equal(Gid, row.ManifestGid);
        Assert.Equal(SteamUpdated, row.SteamUpdatedAt);
    }

    [Fact]
    public async Task ANewBuildTheReleaseAlreadyCovers_IsRecordedAsDone()
    {
        var rig = new Rig();
        rig.Shipped.Builds.Add(26430253);

        var result = await rig.PollAsync();

        Assert.Equal(new LivemapPollResult(LivemapPollOutcome.Shipped, 26430253), result);
        Assert.Equal(LivemapBuildState.Done, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task ABuildThatIsAlreadyRecorded_IsLeftAlone_WithoutAskingGitHub()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        rig.Shipped.Builds.Add(26430253);

        var result = await rig.PollAsync();

        Assert.Equal(new LivemapPollResult(LivemapPollOutcome.Unchanged, 26430253), result);
        Assert.Equal(0, rig.Shipped.Calls);
        Assert.Equal(LivemapBuildState.Detected, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task PollingTwice_RecordsTheBuildOnce()
    {
        var rig = new Rig();

        await rig.PollAsync();
        var second = await rig.PollAsync();

        Assert.Equal(LivemapPollOutcome.Unchanged, second.Outcome);
        Assert.Equal(1, await rig.RowCountAsync());
    }

    [Fact]
    public async Task WhenSteamFails_ThePollFails_AndNothingIsRecorded()
    {
        var rig = new Rig();
        rig.Steam.Failure = new SteamAppInfoException("down");

        await Assert.ThrowsAsync<SteamAppInfoException>(rig.PollAsync);

        Assert.Equal(0, await rig.RowCountAsync());
    }

    [Fact]
    public async Task WhenTheShippedListCannotBeRead_ThePollFails_AndNothingIsRecorded()
    {
        var rig = new Rig();
        rig.Shipped.Failure = new ShippedBuildsException("unreadable");

        await Assert.ThrowsAsync<ShippedBuildsException>(rig.PollAsync);

        Assert.Equal(0, await rig.RowCountAsync());
    }

    [Fact]
    public async Task ANewBuildTheReleaseDoesNotCover_IsStarted()
    {
        var rig = new Rig();

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
    }

    [Fact]
    public async Task ANewBuildTheReleaseAlreadyCovers_IsNotStarted()
    {
        var rig = new Rig();
        rig.Shipped.Builds.Add(26430253);

        await rig.PollAsync();

        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task ABuildStillDetected_IsStartedAgainByTheNextPoll()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);

        var result = await rig.PollAsync();

        Assert.Equal(LivemapPollOutcome.Unchanged, result.Outcome);
        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
    }

    [Theory]
    [InlineData(LivemapBuildState.Done)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Superseded)]
    [InlineData(LivemapBuildState.Merged)]
    [InlineData(LivemapBuildState.PullRequestWaiting)]
    public async Task ABuildThatIsNotInFlight_IsNotStarted(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, state, Ct);

        await rig.PollAsync();

        Assert.Empty(rig.Starter.Started);
        Assert.Equal(state, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Theory]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestOpen)]
    [InlineData(LivemapBuildState.Merging)]
    public async Task ABuildWithItsPullRequestStillToDo_IsStartedAgainByTheNextPoll(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, state, Ct);

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
        Assert.Equal(state, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task ABuildWaitingOnItsPullRequest_IsResumedAndStarted_OnceAutoMergeIsOn()
    {
        var rig = new Rig();
        rig.Config.AutoMerge = true;
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.PullRequestWaiting, Ct);

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
        Assert.Equal(LivemapBuildState.PullRequestOpen, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task AParkedBuildWithAPullRequest_IsResumedToThePullRequest_AndStarted()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Classified, Ct);
        Assert.True(await rig.Store.OpenPullRequestAsync(26430253, 7, new string('a', 40), waiting: false, Ct));
        await rig.Store.ParkAsync(26430253, "busy", 3, TimeSpan.Zero, Ct);

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
        Assert.Equal(LivemapBuildState.PullRequestOpen, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    private static async Task WithPullRequestAsync(Rig rig, long buildId, int number, bool waiting)
    {
        await rig.Store.RecordAsync(buildId, Gid, SteamUpdated, LivemapBuildState.Classified, Ct);
        Assert.True(await rig.Store.OpenPullRequestAsync(buildId, number, new string('a', 40), waiting, Ct));
        rig.PullRequests.Seed(new LivemapPullRequest(number, "open", false, false, "main", FakeLivemapPullRequests.Fork, LivemapBuildBranch.For(buildId), FakeLivemapPullRequests.Fork, new string('a', 40), FakeLivemapPullRequests.AppLogin, "Bot", 1));
        rig.PullRequests.Branches[LivemapBuildBranch.For(buildId)] = new string('a', 40);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuildAnotherOneReplaces_LosesItsPullRequestAndBranch(bool waiting)
    {
        var rig = new Rig();
        await WithPullRequestAsync(rig, 100, 7, waiting);

        await rig.PollAsync();

        Assert.Equal([7], rig.PullRequests.Closed);
        Assert.Equal([LivemapBuildBranch.For(100)], rig.PullRequests.Deleted);
        var row = (await rig.Store.FindAsync(100, Ct))!;
        Assert.Equal(LivemapBuildState.Superseded, row.State);
        Assert.Null(row.PullRequestNumber);
        Assert.Null(row.HeadSha);
    }

    [Fact]
    public async Task ABuildAnotherOneReplaces_IsClosedOnce_NoMatterHowOftenThePollRuns()
    {
        var rig = new Rig();
        await WithPullRequestAsync(rig, 100, 7, waiting: false);

        await rig.PollAsync();
        await rig.PollAsync();

        Assert.Equal([7], rig.PullRequests.Closed);
        Assert.Single(rig.PullRequests.Deleted);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("gone")]
    public async Task AReplacedPullRequestThatIsNotOpen_IsNotClosed_ButItsBranchGoesAndTheBuildIsReleased(string how)
    {
        var rig = new Rig();
        await WithPullRequestAsync(rig, 100, 7, waiting: false);
        if (how == "closed")
        {
            rig.PullRequests.Pulls[7] = rig.PullRequests.Pulls[7] with { State = "closed", Merged = true };
        }
        else
        {
            rig.PullRequests.Pulls.Remove(7);
        }

        await rig.PollAsync();

        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal([LivemapBuildBranch.For(100)], rig.PullRequests.Deleted);
        Assert.Null((await rig.Store.FindAsync(100, Ct))!.PullRequestNumber);
    }

    private static async Task ClassifiedWithoutANumberAsync(Rig rig, long buildId)
    {
        await rig.Store.RecordAsync(buildId, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        Assert.True(await rig.Store.ClassifyAsync(buildId, LivemapClassification.Easy, 777, Ct));
    }

    [Fact]
    public async Task AReplacedBuildThatNeverStoredItsPullRequestNumber_LosesTheOpenPullRequestOnItsBranch()
    {
        var rig = new Rig();
        await ClassifiedWithoutANumberAsync(rig, 100);
        var leftover = rig.PullRequests.Leftover(LivemapBuildBranch.For(100));

        await rig.PollAsync();
        await rig.PollAsync();

        Assert.Equal([leftover.Number], rig.PullRequests.Closed);
        Assert.Equal([LivemapBuildBranch.For(100)], rig.PullRequests.Deleted);
        var row = (await rig.Store.FindAsync(100, Ct))!;
        Assert.Equal(LivemapBuildState.Superseded, row.State);
        Assert.Null(row.Classification);
    }

    [Fact]
    public async Task AReplacedBuildWithABranchButNoPullRequest_HasItsBranchDeleted()
    {
        var rig = new Rig();
        await ClassifiedWithoutANumberAsync(rig, 100);
        rig.PullRequests.Branches[LivemapBuildBranch.For(100)] = new string('a', 40);

        await rig.PollAsync();

        Assert.Empty(rig.PullRequests.Closed);
        Assert.Equal([LivemapBuildBranch.For(100)], rig.PullRequests.Deleted);
        Assert.Empty(rig.PullRequests.Branches);
    }

    [Fact]
    public async Task OlderBuildsStillMerging_AreStartedAgainByEveryPoll_UntilTheyFinish()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(100, "g100", SteamUpdated, LivemapBuildState.Merging, Ct);
        await rig.Store.RecordAsync(200, "g200", SteamUpdated, LivemapBuildState.Merging, Ct);

        await rig.PollAsync();
        await rig.PollAsync();

        Assert.Equal(
            [(26430253L, Gid), (100L, "g100"), (200L, "g200"), (26430253L, Gid), (100L, "g100"), (200L, "g200")],
            rig.Starter.Started);
        Assert.Equal(LivemapBuildState.Merging, (await rig.Store.FindAsync(100, Ct))!.State);
    }

    [Fact]
    public async Task TheCurrentBuildMerging_IsStartedOnce()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Merging, Ct);

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
    }

    [Fact]
    public async Task OlderBuildsStillMerging_AreStartedBeforeAnyPullRequestIsReleased()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(100, "g100", SteamUpdated, LivemapBuildState.Merging, Ct);
        await WithPullRequestAsync(rig, 50, 7, waiting: false);
        rig.PullRequests.Hook = _ => throw new InvalidOperationException("GitHub is down");

        await Assert.ThrowsAsync<InvalidOperationException>(rig.PollAsync);

        Assert.Equal([26430253L, 100L], rig.Starter.Started.Select(s => s.BuildId));
    }

    [Fact]
    public async Task OlderBuildsWithoutAPullRequest_CostNoCallToTheFork()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(100, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        await rig.Store.RecordAsync(200, Gid, SteamUpdated, LivemapBuildState.Parked, Ct);

        await rig.PollAsync();

        Assert.Empty(rig.PullRequests.Calls);
    }

    [Fact]
    public async Task WhenAReplacedPullRequestCannotBeClosed_TheNewBuildIsStartedAnyway_AndTheNextPollTriesAgain()
    {
        var rig = new Rig();
        await WithPullRequestAsync(rig, 100, 7, waiting: false);
        var failing = true;
        rig.PullRequests.Hook = call => failing && call.StartsWith("close:", StringComparison.Ordinal)
            ? Task.FromException(new InvalidOperationException("GitHub is down"))
            : Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(rig.PollAsync);

        Assert.Equal(26430253, Assert.Single(rig.Starter.Started).BuildId);
        Assert.NotNull((await rig.Store.FindAsync(100, Ct))!.PullRequestNumber);

        failing = false;
        await rig.PollAsync();

        Assert.Equal([7], rig.PullRequests.Closed);
        Assert.Null((await rig.Store.FindAsync(100, Ct))!.PullRequestNumber);
    }

    [Fact]
    public async Task AParkedBuild_WhoseTimeHasCome_IsResumedAndStarted()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        await rig.Store.ParkAsync(26430253, "busy", 3, TimeSpan.Zero, Ct);

        await rig.PollAsync();

        Assert.Equal((26430253L, Gid), Assert.Single(rig.Starter.Started));
        Assert.Equal(LivemapBuildState.Detected, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task AParkedBuild_BeforeItsTime_IsLeftParked()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        await rig.Store.ParkAsync(26430253, "busy", 3, TimeSpan.FromMinutes(10), Ct);

        await rig.PollAsync();

        Assert.Empty(rig.Starter.Started);
        Assert.Equal(LivemapBuildState.Parked, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task ANewerBuild_ReplacesTheOlderOnesStillInFlight_AndOnlyThose()
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(100, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        await rig.Store.RecordAsync(200, Gid, SteamUpdated, LivemapBuildState.Parked, Ct);
        await rig.Store.RecordAsync(300, Gid, SteamUpdated, LivemapBuildState.Classified, Ct);
        await rig.Store.RecordAsync(400, Gid, SteamUpdated, LivemapBuildState.NeedsOwner, Ct);
        await rig.Store.RecordAsync(500, Gid, SteamUpdated, LivemapBuildState.Done, Ct);

        await rig.PollAsync();

        var states = new List<LivemapBuildState>();
        foreach (var id in new long[] { 100, 200, 300, 400, 500 })
        {
            states.Add((await rig.Store.FindAsync(id, Ct))!.State);
        }

        Assert.Equal(
            [LivemapBuildState.Superseded, LivemapBuildState.Superseded, LivemapBuildState.Superseded, LivemapBuildState.NeedsOwner, LivemapBuildState.Done],
            states);
        Assert.Equal(26430253, Assert.Single(rig.Starter.Started).BuildId);
    }

    [Fact]
    public async Task WhenTheWorkflowCannotBeStarted_ThePollFails_ButTheBuildIsKept()
    {
        var rig = new Rig();
        rig.Starter.Failure = new InvalidOperationException("Temporal is down");

        await Assert.ThrowsAsync<InvalidOperationException>(rig.PollAsync);

        Assert.Equal(LivemapBuildState.Detected, (await rig.Store.FindAsync(26430253, Ct))!.State);
    }

    [Fact]
    public async Task WhenSteamFails_NothingIsStarted()
    {
        var rig = new Rig();
        rig.Steam.Failure = new SteamAppInfoException("down");

        await Assert.ThrowsAsync<SteamAppInfoException>(rig.PollAsync);

        Assert.Empty(rig.Starter.Started);
    }
}
