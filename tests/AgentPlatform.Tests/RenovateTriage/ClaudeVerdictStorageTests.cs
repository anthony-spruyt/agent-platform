using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeVerdictStorageTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TriageVerdict.Safe, "Safe")]
    [InlineData(TriageVerdict.Fixable, "Fixable")]
    [InlineData(TriageVerdict.Blocked, "Blocked")]
    [InlineData(TriageVerdict.Owner, "Owner")]
    public void TheClaudeVerdicts_SerialiseAsTheirNames(TriageVerdict verdict, string name) =>
        Assert.Equal($"\"{name}\"", System.Text.Json.JsonSerializer.Serialize(verdict));

    [Fact]
    public async Task Record_StoresTheRungOfTheVerdict()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "claude-safe", null), TriageMode.Live, Ct, LadderRung.Claude);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.Equal(LadderRung.Claude, row.Rung);
        Assert.Null(row.Action);
    }

    [Fact]
    public async Task Record_WithoutARung_LeavesItEmpty()
    {
        var data = InMemoryRunData.Create();

        await data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, null), TriageMode.Live, Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Null((await db.TriageVerdicts.SingleAsync(Ct)).Rung);
    }

    [Fact]
    public async Task MarkApplied_StoresWhatApplyDid()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "claude-safe", null), TriageMode.Live, Ct, LadderRung.Claude);

        await data.Verdicts.MarkAppliedAsync(Repo, 7, Sha, TriageVerdict.Safe, Ct, VerdictAction.Approve);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.TriageVerdicts.SingleAsync(Ct);
        Assert.NotNull(row.AppliedAt);
        Assert.Equal(VerdictAction.Approve, row.Action);
    }

    [Fact]
    public async Task ExactlyOneClaudeVerdictIsKeptPerHead()
    {
        var data = InMemoryRunData.Create();
        await data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Fixable, "claude-fixable", "fix it"), TriageMode.Live, Ct, LadderRung.Claude);

        var second = await data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "claude-safe", null), TriageMode.Live, Ct, LadderRung.Claude);

        Assert.False(second);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(TriageVerdict.Fixable, (await db.TriageVerdicts.SingleAsync(Ct)).Verdict);
    }

    [Fact]
    public async Task TheVerdictColumnFitsTheLongestClaudeVerdict()
    {
        await using var db = await InMemoryRunData.Create().Factory.CreateDbContextAsync(Ct);
        var property = db.Model.FindEntityType(typeof(RenovateTriageVerdict))!.FindProperty(nameof(RenovateTriageVerdict.Verdict))!;

        Assert.True(Enum.GetNames<TriageVerdict>().Max(n => n.Length) <= property.GetMaxLength());
    }
}
