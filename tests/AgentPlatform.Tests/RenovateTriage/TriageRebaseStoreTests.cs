using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageRebaseStoreTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string _otherSha = new('b', 40);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TriageRebaseStore Store, InMemoryRunData Data, ManualTime Time) Create()
    {
        var data = InMemoryRunData.Create();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero));
        return (new TriageRebaseStore(data.Factory, time), data, time);
    }

    [Fact]
    public async Task Find_WithoutARecord_ReturnsNull()
    {
        var (store, _, _) = Create();

        Assert.Null(await store.FindAsync(Repo, 7, Ct));
    }

    [Fact]
    public async Task Record_StoresTheHeadTheBoxWasTickedOn()
    {
        var (store, _, time) = Create();

        await store.RecordAsync(Repo, 7, Sha, Ct);

        var row = await store.FindAsync(Repo, 7, Ct);
        Assert.NotNull(row);
        Assert.Equal(Sha, row.HeadSha);
        Assert.Equal(time.Now, row.RequestedAt);
    }

    [Fact]
    public async Task Record_WhenThePullRequestWasAlreadyRebased_KeepsTheFirstRecord()
    {
        var (store, _, time) = Create();
        await store.RecordAsync(Repo, 7, Sha, Ct);
        time.Now = time.Now.AddHours(3);

        await store.RecordAsync(Repo, 7, _otherSha, Ct);

        var row = await store.FindAsync(Repo, 7, Ct);
        Assert.NotNull(row);
        Assert.Equal(Sha, row.HeadSha);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero), row.RequestedAt);
    }

    [Fact]
    public async Task Record_KeepsEachPullRequestSeparate()
    {
        var (store, _, _) = Create();

        await store.RecordAsync(Repo, 7, Sha, Ct);

        Assert.Null(await store.FindAsync(Repo, 8, Ct));
        Assert.Null(await store.FindAsync("anthony-spruyt/other", 7, Ct));
    }

    [Fact]
    public async Task Record_RetriesOnceWhenTheSaveRacesAnotherWriter()
    {
        var (store, data, _) = Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        await store.RecordAsync(Repo, 7, Sha, Ct);

        Assert.NotNull(await store.FindAsync(Repo, 7, Ct));
    }

    [Theory]
    [InlineData("", 7, Sha)]
    [InlineData(Repo, 0, Sha)]
    [InlineData(Repo, 7, "")]
    public async Task Record_RejectsBlankInput(string repo, int prNumber, string headSha)
    {
        var (store, _, _) = Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.RecordAsync(repo, prNumber, headSha, Ct));
    }
}
