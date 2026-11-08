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
                NullLogger<LivemapPollActivities>.Instance);
        }

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
}
