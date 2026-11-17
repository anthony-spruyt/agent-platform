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

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

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

        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, null, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).Comment);
    }

    [Fact]
    public async Task Record_TheSameCommitTwice_KeepsTheFirstVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Shadow, null, Ct);

        Assert.False(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, (await db.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    [Fact]
    public async Task Record_ANewCommitOrAnotherPr_IsANewVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, new string('a', 40), NeedsReview(), TriageMode.Shadow, null, Ct));
        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 8, Sha, NeedsReview(), TriageMode.Shadow, null, Ct));
        Assert.True(await data.Verdicts.RecordAsync("anthony-spruyt/other", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct));
    }

    [Fact]
    public async Task Exists_SeesARecordedCommitOnly()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, new string('b', 40), TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 8, Sha, TriageMode.Shadow, Ct));
    }

    [Fact]
    public async Task Record_TheSameCommitInAnotherMode_IsANewVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, null, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(2, await db.TriageVerdicts.CountAsync(Ct));
    }

    [Fact]
    public async Task Exists_FiltersOnTheMode()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task Exists_InLive_CountsOnlyAppliedRows()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, null, Ct);

        Assert.False(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task FindUnapplied_ReturnsTheStoredDecisionOfAnUnappliedLiveRow()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, null, Ct);

        var decision = await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct);

        Assert.Equal(NeedsReview(), decision);
    }

    [Fact]
    public async Task FindUnapplied_IgnoresShadowRowsAppliedRowsAndOtherCommits()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 8, Sha, NeedsReview(), TriageMode.Live, null, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 8, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 8, Sha, Ct));
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 9, Sha, Ct));
    }

    [Fact]
    public async Task MarkApplied_StampsOnlyTheLiveRowAndKeepsTheFirstStamp()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Live, null, Ct);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var stamp = (await db.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Live, Ct)).AppliedAt;
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

        Assert.NotNull(stamp);
        await using var after = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(stamp, (await after.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Live, Ct)).AppliedAt);
        Assert.Null((await after.TriageVerdicts.SingleAsync(v => v.Mode == TriageMode.Shadow, Ct)).AppliedAt);
    }

    [Fact]
    public async Task MarkApplied_LeavesARowOfAnotherVerdictUnapplied()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), TriageMode.Live, null, Ct);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).AppliedAt);
    }

    [Fact]
    public async Task MarkApplied_WithoutARow_DoesNothing()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Empty(await db.TriageVerdicts.ToListAsync(Ct));
    }

    [Fact]
    public async Task MarkAppliedAs_StoresTheEffectiveVerdictReasonAndCommentOnTheRowOfTheOriginalVerdict()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Safe, "agent", "agent text"), TriageMode.Live, null, Ct);
        var effective = NeedsReview();

        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Safe, effective, VerdictAction.Comment, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal([TriageRules.CiFailed], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal(effective.Comment, row.Comment);
        Assert.NotNull(row.AppliedAt);
        Assert.Equal(VerdictAction.Comment, row.Action);
        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task MarkAppliedAs_LeavesARowOfAnotherVerdictAndAnAppliedRowAlone()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.ChangelogPresent, null), TriageMode.Live, null, Ct);

        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Safe, NeedsReview(), VerdictAction.Comment, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, row.Verdict);
        Assert.Null(row.AppliedAt);

        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, VerdictAction.Approve, Ct);
        await data.Verdicts.MarkAppliedAsAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, NeedsReview(), VerdictAction.Comment, Ct);
        await using var after = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, (await after.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    private static TriageDecision Withdrawn() => TriageRules.ApprovalWithdrawnDecision("anthony-spruyt/widgets", Sha, []);

    [Fact]
    public async Task Withdraw_OverwritesTheAppliedRowInPlaceAndKeepsItApplied()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Safe, "agent", "agent text"), TriageMode.Live, LadderRung.Claude, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Safe, VerdictAction.Approve, Ct);
        var withdrawn = Withdrawn();

        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, withdrawn, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal([TriageRules.ApprovalWithdrawn], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal(withdrawn.Comment, row.Comment);
        Assert.Equal(VerdictAction.Comment, row.Action);
        Assert.Equal(LadderRung.Rule, row.Rung);
        Assert.NotNull(row.AppliedAt);
        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
    }

    [Fact]
    public async Task Withdraw_KeepsTheTimeTheApprovalWasApplied()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Live, null, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, VerdictAction.Approve, Ct);
        await using var before = await data.Factory.CreateDbContextAsync(Ct);
        var appliedAt = (await before.TriageVerdicts.SingleAsync(Ct)).AppliedAt;

        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, Withdrawn(), Ct);

        await using var after = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(appliedAt, (await after.TriageVerdicts.SingleAsync(Ct)).AppliedAt);
    }

    [Fact]
    public async Task Withdraw_AnUnappliedRow_BecomesTheAppliedWithdrawal()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Live, null, Ct);

        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, Withdrawn(), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.NotNull(row.AppliedAt);
    }

    [Fact]
    public async Task Withdraw_WithoutARow_StoresTheAppliedWithdrawal()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, Withdrawn(), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageMode.Live, row.Mode);
        Assert.Equal(TriageVerdict.NeedsReview, row.Verdict);
        Assert.Equal(VerdictAction.Comment, row.Action);
        Assert.NotNull(row.AppliedAt);
        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task Withdraw_TwiceOrBesideAShadowRowOrAnotherCommit_LeavesOneRowEach()
    {
        var data = InMemoryRunData.Create();
        var other = new string('a', 40);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, other, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Live, null, Ct);

        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, Withdrawn(), Ct);
        await data.Verdicts.WithdrawAsync("anthony-spruyt/widgets", 7, Sha, Withdrawn(), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var rows = await db.TriageVerdicts.ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.Equal([TriageRules.CiFailed], JsonSerializer.Deserialize<string[]>(Assert.Single(rows, r => r.Mode == TriageMode.Shadow).Reasons)!);
        Assert.Null(Assert.Single(rows, r => r.HeadSha == other).AppliedAt);
        Assert.Equal([TriageRules.ApprovalWithdrawn], JsonSerializer.Deserialize<string[]>(Assert.Single(rows, r => r.Mode == TriageMode.Live && r.HeadSha == Sha).Reasons)!);
    }

    private static TriageDecision Wait() => TriageRules.CiTimeoutDecision() with { Verdict = TriageVerdict.Owner };

    private static TriageDecision Approve() => new(TriageVerdict.Approve, TriageRules.ChangelogPresent, "**Auto-approved:** patch bump.");

    private static async Task<DateTimeOffset> BackdateAsync(InMemoryRunData data, TimeSpan age)
    {
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var at = DateTimeOffset.UtcNow - age;
        foreach (var row in await db.TriageVerdicts.ToListAsync(Ct))
        {
            row.CreatedAt = at;
        }

        await db.SaveChangesAsync(Ct);
        return at;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewVerdict_ReplacesATransientRow(bool applied)
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Wait(), TriageMode.Live, null, Ct);
        if (applied)
        {
            Assert.True(await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 7, Sha, Wait(), Ct));
        }

        var createdAt = await BackdateAsync(data, TimeSpan.FromHours(5));

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Approve(), TriageMode.Live, LadderRung.Jev, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, row.Verdict);
        Assert.Equal([TriageRules.ChangelogPresent], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal(Approve().Comment, row.Comment);
        Assert.Equal(LadderRung.Jev, row.Rung);
        Assert.Null(row.AppliedAt);
        Assert.Null(row.Action);
        Assert.Equal(createdAt, row.CreatedAt);
        Assert.Equal(Approve(), await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
    }

    [Theory]
    [InlineData(TriageMode.Live, false)]
    [InlineData(TriageMode.Live, true)]
    [InlineData(TriageMode.Shadow, false)]
    public async Task ANewVerdict_NeverReplacesARealOrShadowRow(TriageMode mode, bool applied)
    {
        var data = InMemoryRunData.Create();
        var first = mode == TriageMode.Shadow ? Wait() : NeedsReview();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, first, mode, null, Ct);
        if (applied)
        {
            await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, first.Verdict, VerdictAction.Comment, Ct);
        }

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Approve(), mode, null, Ct);

        Assert.False(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(first.Verdict, row.Verdict);
        Assert.Equal(first.Comment, row.Comment);
        Assert.Equal(applied, row.AppliedAt is not null);
    }

    [Fact]
    public async Task IsJudged_InLive_DoesNotCountATransientRow_ButExistsStillDoes()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Wait(), TriageMode.Live, null, Ct);
        await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 7, Sha, Wait(), Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 8, Sha, NeedsReview(), TriageMode.Live, null, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 8, Sha, TriageVerdict.NeedsReview, VerdictAction.Comment, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 9, Sha, NeedsReview(), TriageMode.Live, null, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 10, Sha, Wait(), TriageMode.Shadow, null, Ct);

        Assert.False(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
        Assert.True(await data.Verdicts.ExistsAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
        Assert.True(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 8, Sha, TriageMode.Live, Ct));
        Assert.False(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 9, Sha, TriageMode.Live, Ct));
        Assert.True(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 10, Sha, TriageMode.Shadow, Ct));
        Assert.False(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 11, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task FindUnapplied_SkipsATransientRow()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Wait(), TriageMode.Live, null, Ct);

        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkTransient_OverwritesAnUnappliedRowInPlace_AndLeavesNothingUnapplied(bool transientBefore)
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, transientBefore ? Wait() : Approve(), TriageMode.Live, LadderRung.Claude, Ct);
        var createdAt = await BackdateAsync(data, TimeSpan.FromDays(1));
        var wait = TriageRules.CiTimeoutDecision();

        Assert.True(await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 7, Sha, wait, Ct));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(wait.Verdict, row.Verdict);
        Assert.Equal([TriageRules.CiTimeout], JsonSerializer.Deserialize<string[]>(row.Reasons)!);
        Assert.Equal(wait.Comment, row.Comment);
        Assert.Equal(LadderRung.Rule, row.Rung);
        Assert.NotNull(row.AppliedAt);
        Assert.Equal(createdAt, row.CreatedAt);
        Assert.Null(await data.Verdicts.FindUnappliedAsync("anthony-spruyt/widgets", 7, Sha, Ct));
    }

    [Fact]
    public async Task MarkTransient_WithoutARow_StoresTheWait()
    {
        var data = InMemoryRunData.Create();

        Assert.True(await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 7, Sha, TriageRules.CiTimeoutDecision(), Ct));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageMode.Live, row.Mode);
        Assert.Equal(TriageRules.CiTimeoutComment, row.Comment);
    }

    [Fact]
    public async Task MarkTransient_NeverOverwritesARealAppliedRow()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Approve(), TriageMode.Live, null, Ct);
        await data.Verdicts.MarkAppliedAsync("anthony-spruyt/widgets", 7, Sha, TriageVerdict.Approve, VerdictAction.Approve, Ct);

        Assert.False(await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 7, Sha, TriageRules.CiTimeoutDecision(), Ct));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(TriageVerdict.Approve, row.Verdict);
        Assert.Equal(VerdictAction.Approve, row.Action);
    }

    [Fact]
    public async Task ListWaits_ReturnsOnlyLiveWaitsCreatedInsideTheWindow()
    {
        var data = InMemoryRunData.Create();
        var bodyUnchecked = TriageRules.PrecheckDecision(PrecheckVerdict.BodyUnchecked);
        await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 1, Sha, Wait(), Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 2, Sha, bodyUnchecked, TriageMode.Live, null, Ct);
        await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 3, Sha, TriageRules.HandOff(Wait()), Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 4, Sha, Wait(), TriageMode.Shadow, null, Ct);
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 5, Sha, NeedsReview() with { Comment = TriageRules.CiTimeoutComment }, TriageMode.Live, null, Ct);
        await BackdateAsync(data, TimeSpan.FromDays(4));
        await data.Verdicts.MarkTransientAsync("anthony-spruyt/widgets", 6, Sha, Wait(), Ct);
        await using (var db = await data.Factory.CreateDbContextAsync(Ct))
        {
            (await db.TriageVerdicts.SingleAsync(v => v.PrNumber == 6, Ct)).CreatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var waits = await data.Verdicts.ListWaitsAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(5), DateTimeOffset.UtcNow - TimeSpan.FromDays(3), Ct);

        Assert.Equal(
            [new TriageWait("anthony-spruyt/widgets", 1, Sha, Wait()), new TriageWait("anthony-spruyt/widgets", 2, Sha, bodyUnchecked)],
            waits.OrderBy(w => w.PrNumber));
        Assert.Empty(await data.Verdicts.ListWaitsAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(10), DateTimeOffset.UtcNow - TimeSpan.FromDays(6), Ct));
    }

    [Fact]
    public async Task EndWait_RecordsTheHandOff_SoTheRowIsNoLongerAWait()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Wait(), TriageMode.Live, null, Ct);
        await BackdateAsync(data, TimeSpan.FromDays(4));
        var handOff = TriageRules.HandOff(Wait());

        await data.Verdicts.EndWaitAsync("anthony-spruyt/widgets", 7, Sha, handOff, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(handOff.Comment, row.Comment);
        Assert.NotNull(row.AppliedAt);
        Assert.Empty(await data.Verdicts.ListWaitsAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(10), DateTimeOffset.UtcNow, Ct));
        Assert.False(await data.Verdicts.IsJudgedAsync("anthony-spruyt/widgets", 7, Sha, TriageMode.Live, Ct));
    }

    [Fact]
    public async Task EndWait_LeavesARowThatIsNoLongerAWaitAlone()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, Approve(), TriageMode.Live, null, Ct);

        await data.Verdicts.EndWaitAsync("anthony-spruyt/widgets", 7, Sha, TriageRules.HandOff(Wait()), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(Approve().Comment, row.Comment);
        Assert.Null(row.AppliedAt);
    }

    [Fact]
    public async Task Record_RetriesOnceWhenTheWriteFailsTransiently()
    {
        var data = InMemoryRunData.Create();
        data.FailNextSaveWith(new DbUpdateException("conflict"));

        var inserted = await data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct);

        Assert.True(inserted);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Single(await db.TriageVerdicts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Record_APersistentWriteFailure_Surfaces()
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(new DbUpdateException("down"));

        await Assert.ThrowsAsync<DbUpdateException>(() => data.Verdicts.RecordAsync("anthony-spruyt/widgets", 7, Sha, NeedsReview(), TriageMode.Shadow, null, Ct));
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
