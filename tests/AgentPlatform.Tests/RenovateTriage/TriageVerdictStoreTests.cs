using System.Text.Json;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageVerdictStoreTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TriageDecision NeedsReview() => new(TriageVerdict.NeedsReview, TriageRules.CiFailed, TriageRules.CiFailedComment);

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost").Options;
        using var db = new RunDataDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public async Task Record_StoresTheVerdictTheReasonsAndTheWouldBeComment()
    {
        var data = InMemoryRunData.Create();

        var inserted = await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal("octo/widgets", row.Repo);
        Assert.Equal(7, row.PrNumber);
        Assert.Equal(Sha, row.HeadSha);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal(["ci-failed"], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal("Flagged for review as CI failed.", row.Comment);
        Assert.Equal(TriageMode.Shadow, row.Mode);
    }

    [Fact]
    public async Task Record_KeepsNoCommentWhenThereIsNone()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).Comment);
    }

    [Fact]
    public async Task Record_TheSameCommitTwice_KeepsTheFirstVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        var inserted = await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, Ct);

        Assert.False(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, (await db.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    [Fact]
    public async Task Record_ANewCommitOrAnotherPr_IsANewVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(await data.Verdicts.RecordAsync("octo/widgets", 7, new string('a', 40), NeedsReview(), TriageMode.Shadow, Ct));
        Assert.True(await data.Verdicts.RecordAsync("octo/widgets", 8, Sha, NeedsReview(), TriageMode.Shadow, Ct));
        Assert.True(await data.Verdicts.RecordAsync("octo/other", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct));
    }

    [Fact]
    public async Task Exists_SeesARecordedCommitOnly()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("octo/widgets", 7, Sha, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("octo/widgets", 7, new string('b', 40), Ct));
        Assert.False(await data.Verdicts.ExistsAsync("octo/widgets", 8, Sha, Ct));
    }

    [Fact]
    public async Task Record_RetriesOnceWhenTheWriteFailsTransiently()
    {
        var data = InMemoryRunData.Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        var inserted = await data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Single(await db.TriageVerdicts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Record_APersistentWriteFailure_Surfaces()
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(new DbUpdateException("down"));

        await Assert.ThrowsAsync<DbUpdateException>(() => data.Verdicts.RecordAsync("octo/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct));
    }

    [Fact]
    public void TheTable_IsRenovateTriageVerdictsWithTheDedupeKey()
    {
        var type = Model().FindEntityType(typeof(RenovateTriageVerdict))!;

        Assert.Equal("renovate_triage_verdicts", type.GetTableName());
        var columns = type.GetProperties().Select(p => p.GetColumnName(StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema()))!).ToHashSet();
        Assert.Superset(new HashSet<string> { "id", "repo", "pr_number", "head_sha", "verdict", "reasons", "comment", "mode", "created_at" }, columns);
        var unique = Assert.Single(type.GetIndexes(), i => i.IsUnique);
        Assert.Equal(["Repo", "PrNumber", "HeadSha"], unique.Properties.Select(p => p.Name));
    }
}
