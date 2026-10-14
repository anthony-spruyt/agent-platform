using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.Sweeper;

public class TriagePendingSweeperTests
{
    private const string Repo = "owner/repo";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListOpen_ReturnsPendingAndRedrivenRows_OldestFirst_AndNoSettledOnes()
    {
        var pending = InMemoryRunData.Create().Pending;
        await pending.UpsertAsync(Repo, 3, Sha, "r", Ct);
        await Task.Delay(5, Ct);
        await pending.UpsertAsync(Repo, 1, Sha, "r", Ct);
        await Task.Delay(5, Ct);
        await pending.UpsertAsync(Repo, 2, Sha, "r", Ct);
        await Task.Delay(5, Ct);
        await pending.UpsertAsync(Repo, 4, Sha, "r", Ct);
        await pending.SetStatusAsync(Repo, 1, Sha, TriagePendingStatus.Redriven, Ct);
        await pending.SetStatusAsync(Repo, 4, Sha, TriagePendingStatus.Settled, Ct);

        var open = await pending.ListOpenAsync(Ct);

        Assert.Equal([3, 1, 2], open.Select(p => p.PrNumber));
    }
}
