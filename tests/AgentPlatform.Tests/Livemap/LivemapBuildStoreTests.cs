using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildStoreTests
{
    private const string Gid = "8886672487109033979";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LivemapBuildStore Store(InMemoryRunData data) => new(data.Factory, new FixedTimeProvider(Now));

    [Fact]
    public async Task ABuildThatWasNeverRecorded_IsNotFound()
    {
        var store = Store(InMemoryRunData.Create());

        Assert.Null(await store.FindAsync(26430253, Ct));
    }

    [Theory]
    [InlineData(LivemapBuildState.Detected)]
    [InlineData(LivemapBuildState.Done)]
    public async Task ANewBuild_IsStoredWithItsStateAndTimes(LivemapBuildState state)
    {
        var store = Store(InMemoryRunData.Create());

        var recorded = await store.RecordAsync(26430253, Gid, SteamUpdated, state, Ct);

        var found = await store.FindAsync(26430253, Ct);
        Assert.NotNull(found);
        Assert.Equal(recorded.BuildId, found.BuildId);
        Assert.Equal(26430253, found.BuildId);
        Assert.Equal(Gid, found.ManifestGid);
        Assert.Equal(SteamUpdated, found.SteamUpdatedAt);
        Assert.Equal(state, found.State);
        Assert.Equal(Now, found.CreatedAt);
        Assert.Equal(Now, found.UpdatedAt);
    }

    [Fact]
    public async Task ABuildThatIsAlreadyRecorded_KeepsItsRow()
    {
        var data = InMemoryRunData.Create();
        await Store(data).RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);

        var again = await new LivemapBuildStore(data.Factory, new FixedTimeProvider(Now.AddHours(1)))
            .RecordAsync(26430253, "1", SteamUpdated.AddDays(1), LivemapBuildState.Done, Ct);

        Assert.Equal(LivemapBuildState.Detected, again.State);
        Assert.Equal(Gid, again.ManifestGid);
        Assert.Equal(Now, again.CreatedAt);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.LivemapBuilds.CountAsync(Ct));
    }

    [Fact]
    public async Task AWriteThatLosesARace_IsRetriedOnce()
    {
        var data = InMemoryRunData.Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        await Store(data).RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);

        Assert.NotNull(await Store(data).FindAsync(26430253, Ct));
    }

    [Fact]
    public async Task AWriteThatKeepsFailing_IsReported()
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(new DbUpdateException("down"));

        await Assert.ThrowsAsync<DbUpdateException>(() => Store(data).RecordAsync(26430253, Gid, SteamUpdated, LivemapBuildState.Detected, Ct));
    }
}
