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
            Activities = new LivemapPollActivities(
                Options.Create(new LivemapOptions { Enabled = enabled }),
                Steam,
                Shipped,
                Store,
                Starter,
                NullLogger<LivemapPollActivities>.Instance);
        }

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
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Superseded)]
    public async Task ABuildThatIsNotInFlight_IsNotStarted(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.Store.RecordAsync(26430253, Gid, SteamUpdated, state, Ct);

        await rig.PollAsync();

        Assert.Empty(rig.Starter.Started);
        Assert.Equal(state, (await rig.Store.FindAsync(26430253, Ct))!.State);
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
