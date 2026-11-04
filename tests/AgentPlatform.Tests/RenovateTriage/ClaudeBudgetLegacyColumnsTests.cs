using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeBudgetLegacyColumnsTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARenovateRun_KeepsTheOldColumnsFilled_SoThePreviousImageCanReadIt()
    {
        var data = InMemoryRunData.Create();
        var store = RenovateBudgetFixtures.Store(data.Factory, new FixedTimeProvider(Now));
        var reserved = await store.ReserveAsync("wf", RenovateBudget.Reserve(Guid.NewGuid(), Repo, 7, Sha, "opus"), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.ClaudeRuns.SingleAsync(r => r.Id == reserved.RunId, Ct);

        Assert.Equal((Repo, 7, Sha), (row.LegacyRepo, row.LegacyPrNumber, row.LegacyHeadSha));
    }

    [Fact]
    public async Task AnotherKindsRun_LeavesTheOldColumnsEmpty()
    {
        var data = InMemoryRunData.Create();
        var store = new ClaudeBudgetStore(data.Factory, Options.Create(new ClaudeBudgetOptions()), new FixedTimeProvider(Now), [new TriageAttemptLedger()]);
        var reserved = await store.ReserveAsync("wf", new ReserveClaudeRunInput(Guid.NewGuid(), "other-kind", "thing-1", "thing", "rev-1", "opus"), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.ClaudeRuns.SingleAsync(r => r.Id == reserved.RunId, Ct);

        Assert.Equal((null, null, null), (row.LegacyRepo, row.LegacyPrNumber, row.LegacyHeadSha));
    }
}
