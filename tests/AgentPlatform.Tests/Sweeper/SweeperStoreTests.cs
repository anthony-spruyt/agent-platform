using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.Sweeper;

public class SweeperStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeAnyRun_NothingHasBeenSeen_AndThereIsNoSuccess()
    {
        var store = new SweeperStore(InMemoryRunData.Create().Factory, TimeProvider.System);

        var state = await store.GetAsync(Ct);

        Assert.Null(state.LastSeenDeliveredAt);
        Assert.Null(state.LastSuccessAt);
    }

    [Fact]
    public async Task TheNewestDeliveryAndTheSuccessTime_AreKeptInOneRow()
    {
        var data = InMemoryRunData.Create();
        var time = new FakeTime(T0);
        var store = new SweeperStore(data.Factory, time);

        await store.SaveLastSeenAsync(T0.AddMinutes(-5), Ct);
        await store.SaveSuccessAsync(Ct);

        var state = await store.GetAsync(Ct);
        Assert.Equal(T0.AddMinutes(-5), state.LastSeenDeliveredAt);
        Assert.Equal(T0, state.LastSuccessAt);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.SweeperState.CountAsync(Ct));
    }

    [Fact]
    public async Task AnOlderDelivery_NeverMovesTheMarkBackwards()
    {
        var store = new SweeperStore(InMemoryRunData.Create().Factory, TimeProvider.System);

        await store.SaveLastSeenAsync(T0, Ct);
        await store.SaveLastSeenAsync(T0.AddHours(-1), Ct);

        Assert.Equal(T0, (await store.GetAsync(Ct)).LastSeenDeliveredAt);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
