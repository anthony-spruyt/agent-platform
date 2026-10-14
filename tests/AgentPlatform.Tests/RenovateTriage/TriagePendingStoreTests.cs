using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriagePendingStoreTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string _movedSha = new('a', 40);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TriagePendingStore Store, InMemoryRunData Data, ManualTime Time) Create()
    {
        var data = InMemoryRunData.Create();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 14, 6, 0, 0, TimeSpan.Zero));
        return (new TriagePendingStore(data.Factory, time), data, time);
    }

    [Fact]
    public async Task Upsert_WithoutARow_StartsPendingWithNoAttempts()
    {
        var (store, _, time) = Create();

        var row = await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);

        Assert.Equal((Repo, 7, Sha, "ci-failed", TriagePendingStatus.Pending, 0), (row.Repo, row.PrNumber, row.HeadSha, row.Reason, row.Status, row.Attempts));
        Assert.Equal((time.Now, time.Now), (row.CreatedAt, row.UpdatedAt));
        Assert.True(row.IsOpen);
    }

    [Fact]
    public async Task Upsert_TheSameHeadAgain_KeepsTheStatusAndAttempts()
    {
        var (store, data, time) = Create();
        await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);
        await store.SetStatusAsync(Repo, 7, Sha, TriagePendingStatus.Redriven, Ct);
        await using (var db = await data.Factory.CreateDbContextAsync(Ct))
        {
            (await db.TriagePending.SingleAsync(Ct)).Attempts = 1;
            await db.SaveChangesAsync(Ct);
        }

        time.Now += TimeSpan.FromMinutes(5);
        var row = await store.UpsertAsync(Repo, 7, Sha, "breaking", Ct);

        Assert.Equal((TriagePendingStatus.Redriven, 1, "breaking"), (row.Status, row.Attempts, row.Reason));
        Assert.Equal(time.Now, row.UpdatedAt);
        Assert.Equal(1, await CountAsync(data));
    }

    [Fact]
    public async Task Upsert_AHeadMove_CarriesTheRowToTheNewHeadAndResetsItsAttempts()
    {
        var (store, data, _) = Create();
        var first = await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);
        await using (var db = await data.Factory.CreateDbContextAsync(Ct))
        {
            var stored = await db.TriagePending.SingleAsync(Ct);
            stored.Attempts = 2;
            stored.Status = TriagePendingStatus.Owner;
            await db.SaveChangesAsync(Ct);
        }

        var moved = await store.UpsertAsync(Repo, 7, _movedSha, "major", Ct);

        Assert.Equal(first.Id, moved.Id);
        Assert.Equal((_movedSha, "major", TriagePendingStatus.Pending, 0), (moved.HeadSha, moved.Reason, moved.Status, moved.Attempts));
        Assert.Equal(1, await CountAsync(data));
    }

    [Fact]
    public async Task Upsert_ForAnotherPrOrRepo_IsAnotherRow()
    {
        var (store, data, _) = Create();
        await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);

        await store.UpsertAsync(Repo, 8, Sha, "ci-failed", Ct);
        await store.UpsertAsync("anthony-spruyt/other", 7, Sha, "ci-failed", Ct);

        Assert.Equal(3, await CountAsync(data));
    }

    [Fact]
    public async Task Upsert_RetriesOnceWhenTheWriteFailsTransiently()
    {
        var (store, data, _) = Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        var row = await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);

        Assert.Equal(Sha, row.HeadSha);
        Assert.Equal(1, await CountAsync(data));
    }

    [Theory]
    [InlineData("", 7, Sha, "r")]
    [InlineData(Repo, 0, Sha, "r")]
    [InlineData(Repo, 7, " ", "r")]
    [InlineData(Repo, 7, Sha, "")]
    public async Task Upsert_WithAMissingValue_Throws(string repo, int pr, string sha, string reason)
    {
        var (store, _, _) = Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.UpsertAsync(repo, pr, sha, reason, Ct));
    }

    [Fact]
    public async Task Find_ReturnsTheRowOfThePrWhateverItsHead()
    {
        var (store, _, _) = Create();
        await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);

        Assert.Equal(Sha, (await store.FindAsync(Repo, 7, Ct))!.HeadSha);
        Assert.Null(await store.FindAsync(Repo, 8, Ct));
    }

    [Fact]
    public async Task SetStatus_ChangesTheRowOfThatHeadOnly()
    {
        var (store, _, time) = Create();
        await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);
        time.Now += TimeSpan.FromMinutes(1);

        Assert.False(await store.SetStatusAsync(Repo, 7, _movedSha, TriagePendingStatus.Settled, Ct));
        Assert.Equal(TriagePendingStatus.Pending, (await store.FindAsync(Repo, 7, Ct))!.Status);

        Assert.True(await store.SetStatusAsync(Repo, 7, Sha, TriagePendingStatus.Settled, Ct));
        var row = (await store.FindAsync(Repo, 7, Ct))!;
        Assert.Equal((TriagePendingStatus.Settled, time.Now), (row.Status, row.UpdatedAt));
        Assert.False(row.IsOpen);
    }

    [Theory]
    [InlineData(TriagePendingStatus.Pending, true)]
    [InlineData(TriagePendingStatus.Redriven, true)]
    [InlineData(TriagePendingStatus.Settled, false)]
    [InlineData(TriagePendingStatus.Owner, false)]
    [InlineData(TriagePendingStatus.OwnerHandled, false)]
    public void IsOpen_IsPendingOrRedrivenOnly(TriagePendingStatus status, bool open) =>
        Assert.Equal(open, new TriagePending { Repo = Repo, HeadSha = Sha, Reason = "r", Status = status }.IsOpen);

    [Fact]
    public async Task CountOpen_CountsPendingAndRedrivenRows()
    {
        var (store, _, _) = Create();
        await store.UpsertAsync(Repo, 1, Sha, "r", Ct);
        await store.UpsertAsync(Repo, 2, Sha, "r", Ct);
        await store.UpsertAsync(Repo, 3, Sha, "r", Ct);
        await store.UpsertAsync(Repo, 4, Sha, "r", Ct);
        await store.SetStatusAsync(Repo, 2, Sha, TriagePendingStatus.Redriven, Ct);
        await store.SetStatusAsync(Repo, 3, Sha, TriagePendingStatus.Settled, Ct);
        await store.SetStatusAsync(Repo, 4, Sha, TriagePendingStatus.Owner, Ct);

        Assert.Equal(2, await store.CountOpenAsync(Ct));
    }

    [Fact]
    public async Task AClimbingNeedsReview_LeavesAPendingRowAndNoVerdictRow()
    {
        var (store, data, _) = Create();

        await store.UpsertAsync(Repo, 7, Sha, "ci-failed", Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Single(await db.TriagePending.ToListAsync(Ct));
        Assert.Empty(await db.TriageVerdicts.ToListAsync(Ct));
        Assert.False(await data.Verdicts.ExistsAsync(Repo, 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public void TheTables_AreKeyedAsDesigned()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost").Options;
        using var db = new RunDataDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;

        Assert.Equal("renovate_triage_pending", model.FindEntityType(typeof(TriagePending))!.GetTableName());
        Assert.Equal(["Repo", "PrNumber"], Assert.Single(model.FindEntityType(typeof(TriagePending))!.GetIndexes(), i => i.IsUnique).Properties.Select(p => p.Name));
        Assert.Equal("claude_runs", model.FindEntityType(typeof(ClaudeRun))!.GetTableName());
        Assert.Equal("claude_cooldowns", model.FindEntityType(typeof(ClaudeCooldown))!.GetTableName());
        Assert.Equal("sweeper_state", model.FindEntityType(typeof(SweeperState))!.GetTableName());
    }

    private static async Task<int> CountAsync(InMemoryRunData data)
    {
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        return await db.TriagePending.CountAsync(Ct);
    }
}
