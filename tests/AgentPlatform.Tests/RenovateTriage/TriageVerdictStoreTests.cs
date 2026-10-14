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

    private static TriageDecision NeedsReview() => new(TriageVerdict.NeedsReview, TriageRules.CiFailed, "**Needs review:** CI failed on `0123456`.");

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

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal("anthony-spruyt/widgets", row.Repo);
        Assert.Equal(7, row.PrNumber);
        Assert.Equal(Sha, row.HeadSha);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal(["ci-failed"], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal("**Needs review:** CI failed on `0123456`.", row.Comment);
        Assert.Equal(TriageMode.Shadow, row.Mode);
    }

    [Fact]
    public async Task Record_KeepsNoCommentWhenThereIsNone()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).Comment);
    }

    [Fact]
    public async Task Record_TheSameCommitTwice_KeepsTheFirstVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, Ct);

        Assert.False(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, (await db.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    [Fact]
    public async Task Record_ANewCommitOrAnotherPr_IsANewVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, new string('a', 40), NeedsReview(), TriageMode.Shadow, Ct));
        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 8, Sha, NeedsReview(), TriageMode.Shadow, Ct));
        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/other", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct));
    }

    [Fact]
    public async Task Exists_SeesARecordedCommitOnly()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, new string('b', 40), TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 8, Sha, TriageMode.Shadow, Ct));
    }

    [Fact]
    public async Task Record_TheSameCommitInAnotherMode_IsANewVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(2, await db.TriageVerdicts.CountAsync(Ct));
    }

    [Fact]
    public async Task Exists_FiltersOnTheMode()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task Exists_InLive_CountsOnlyAppliedRows()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, Ct);

        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task FindUnapplied_ReturnsTheStoredDecisionOfAnUnappliedLiveRow()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, Ct);

        var decision = await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct);

        Assert.Equal(NeedsReview(), decision);
    }

    [Fact]
    public async Task FindUnapplied_IgnoresShadowRowsAppliedRowsAndOtherCommits()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 8, Sha, NeedsReview(), TriageMode.Live, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 8, Sha, TriageVerdict.NeedsReview, Ct);

        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 8, Sha, Ct));
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 9, Sha, Ct));
    }

    [Fact]
    public async Task MarkApplied_StampsOnlyTheLiveRowAndKeepsTheFirstStamp()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, Ct);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, Ct);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var stamp = (await db.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Live, Ct)).AppliedAt;
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, Ct);

        Assert.NotNull(stamp);
        await using var after = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(stamp, (await after.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Live, Ct)).AppliedAt);
        Assert.Null((await after.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Shadow, Ct)).AppliedAt);
    }

    [Fact]
    public async Task MarkApplied_LeavesARowOfAnotherVerdictUnapplied()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), TriageMode.Live, Ct);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).AppliedAt);
    }

    [Fact]
    public async Task MarkApplied_WithoutARow_DoesNothing()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Empty(await db.TriageVerdicts.ToListAsync(Ct));
    }

    [Fact]
    public async Task MarkAppliedAs_StoresTheEffectiveVerdictReasonAndCommentOnTheRowOfTheOriginalVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Safe, "agent", "agent text"), TriageMode.Live, Ct);
        var effective = NeedsReview();

        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Safe, effective, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal([TriageRules.CiFailed], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal(effective.Comment, row.Comment);
        Assert.NotNull(row.AppliedAt);
        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task MarkAppliedAs_LeavesARowOfAnotherVerdictAndAnAppliedRowAlone()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), TriageMode.Live, Ct);

        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Safe, NeedsReview(), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, row.Verdict);
        Assert.Null(row.AppliedAt);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, Ct);
        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, NeedsReview(), Ct);
        await using var after = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, (await after.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    [Fact]
    public async Task Record_RetriesOnceWhenTheWriteFailsTransiently()
    {
        var data = InMemoryRunData.Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Single(await db.TriageVerdicts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Record_APersistentWriteFailure_Surfaces()
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(new DbUpdateException("down"));

        await Assert.ThrowsAsync<DbUpdateException>(() => data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, Ct));
    }

    [Fact]
    public void TheTable_IsRenovateTriageVerdictsWithTheDedupeKey()
    {
        var type = Model().FindEntityType(typeof(RenovateTriageVerdict))!;

        Assert.Equal("renovate_triage_verdicts", type.GetTableName());
        var columns = type.GetProperties().Select(p => p.GetColumnName(StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema()))!).ToHashSet();
        Assert.Superset(new HashSet<string> { "id", "repo", "pr_number", "head_sha", "verdict", "reasons", "comment", "mode", "created_at", "applied_at" }, columns);
        var unique = Assert.Single(type.GetIndexes(), i => i.IsUnique);
        Assert.Equal(["Repo", "PrNumber", "HeadSha", "Mode"], unique.Properties.Select(p => p.Name));
    }
}
