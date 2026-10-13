using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Tests.Agents;

public class ReadAppSecretContributorTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 10, 11, 13, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingMinter : IGitHubTokenMinter
    {
        public List<(string Repo, IReadOnlyDictionary<string, string> Permissions)> Calls { get; } = [];

        public Task<InstallationToken> MintTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
        {
            Calls.Add((repo, permissions));
            return Task.FromResult(new InstallationToken("ghs_read", Expiry));
        }
    }

    private static AgentRunRequest Request(AgentRepoContext? repo) =>
        new("wf", "run", "triage", "prompt", AgentTier.Read, 1800, 1, repo);

    [Fact]
    public async Task ContributeAsync_MintsAOneRepoReadTokenForTheRunsRepository()
    {
        var minter = new RecordingMinter();
        var contributor = new ReadAppSecretContributor(minter);

        var contribution = await contributor.ContributeAsync(Request(new AgentRepoContext("octo/widgets", 7, new string('a', 40))), Ct);

        var call = Assert.Single(minter.Calls);
        Assert.Equal("octo/widgets", call.Repo);
        Assert.Equal(
            ["actions", "checks", "contents", "issues", "pull_requests", "statuses"],
            call.Permissions.Keys.Order(StringComparer.Ordinal));
        Assert.All(call.Permissions.Values, v => Assert.Equal("read", v));
        Assert.Equal(Expiry, contribution.ExpiresAt);
    }

    [Fact]
    public async Task ContributeAsync_PutsTheTokenAndAGitCredentialInTheSecretData()
    {
        var contributor = new ReadAppSecretContributor(new RecordingMinter());

        var contribution = await contributor.ContributeAsync(Request(new AgentRepoContext("octo/widgets", 7, new string('a', 40))), Ct);

        Assert.Equal("ghs_read", contribution.Data[AgentPodSpecBuilder.GitHubTokenKey]);
        var credential = new Uri(contribution.Data[AgentPodSpecBuilder.GitCredentialKey].TrimEnd('\n'));
        Assert.Equal("github.com", credential.Host);
        Assert.Equal("x-access-token", credential.UserInfo.Split(':')[0]);
        Assert.Equal("ghs_read", credential.UserInfo.Split(':')[1]);
        Assert.EndsWith("\n", contribution.Data[AgentPodSpecBuilder.GitCredentialKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributeAsync_MintsNothingForARunWithoutARepository()
    {
        var minter = new RecordingMinter();
        var contributor = new ReadAppSecretContributor(minter);

        var contribution = await contributor.ContributeAsync(Request(null), Ct);

        Assert.Empty(minter.Calls);
        Assert.Empty(contribution.Data);
        Assert.Null(contribution.ExpiresAt);
    }
}
