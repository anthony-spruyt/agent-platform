using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Livemap;

public class LivemapWakeActivitiesTests
{
    private const string Gid = "8886672487109033979";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly LivemapBuildWake Wake = new("1111111111111111111111111111111111111111", 77);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class FakeSignaler : ILivemapBuildSignaler
    {
        public List<(long BuildId, LivemapBuildWake Wake)> Signalled { get; } = [];

        public bool Delivered { get; set; } = true;

        public Exception? Failure { get; set; }

        public int FailFirstCalls { get; set; }

        private int _calls;

        public Task<bool> SignalAsync(long buildId, LivemapBuildWake wake, CancellationToken cancellationToken)
        {
            _calls++;
            if (Failure is not null && (FailFirstCalls == 0 || _calls <= FailFirstCalls))
            {
                return Task.FromException<bool>(Failure);
            }

            Signalled.Add((buildId, wake));
            return Task.FromResult(Delivered);
        }
    }

    private sealed class Rig
    {
        public Rig()
        {
            Store = new LivemapBuildStore(InMemoryRunData.Create().Factory, new FixedTimeProvider(Now));
            Activities = new LivemapWakeActivities(Store, Signaler);
        }

        public LivemapBuildStore Store { get; }

        public FakeSignaler Signaler { get; } = new();

        public LivemapWakeActivities Activities { get; }

        public Task<LivemapWakeOutcome> WakeAsync() => new ActivityEnvironment().RunAsync(() => Activities.WakeAsync(Wake));

        public Task<LivemapBuild> SeedAsync(long buildId, LivemapBuildState state) =>
            Store.RecordAsync(buildId, Gid, Now, state, Ct);
    }

    [Fact]
    public async Task WithNoBuildInFlight_NothingIsSignalled()
    {
        var rig = new Rig();

        Assert.Equal(LivemapWakeOutcome.NoBuildInFlight, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Theory]
    [InlineData(LivemapBuildState.Done)]
    [InlineData(LivemapBuildState.Parked)]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Superseded)]
    public async Task ABuildThatIsNotInFlight_IsNotSignalled(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.SeedAsync(400, state);

        Assert.Equal(LivemapWakeOutcome.NoBuildInFlight, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task TheBuildInFlight_IsSignalledWithTheWake()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);

        var outcome = await rig.WakeAsync();

        Assert.Equal(LivemapWakeOutcome.Woken, outcome);
        Assert.Equal((400L, Wake), Assert.Single(rig.Signaler.Signalled));
    }

    [Fact]
    public async Task OnlyTheNewestBuildInFlight_IsSignalled()
    {
        var rig = new Rig();
        await rig.SeedAsync(300, LivemapBuildState.Detected);
        await rig.SeedAsync(400, LivemapBuildState.Detected);

        await rig.WakeAsync();

        Assert.Equal(400, Assert.Single(rig.Signaler.Signalled).BuildId);
    }

    [Fact]
    public async Task WhenNoWorkflowIsRunningForTheBuild_TheWakeEndsQuietly()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.Signaler.Delivered = false;

        Assert.Equal(LivemapWakeOutcome.NotRunning, await rig.WakeAsync());
    }

    [Fact]
    public async Task WhenTheSignalFails_TheActivityFails_SoItIsRetried()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.Signaler.Failure = new InvalidOperationException("Temporal is down");

        await Assert.ThrowsAsync<InvalidOperationException>(rig.WakeAsync);
    }
}
