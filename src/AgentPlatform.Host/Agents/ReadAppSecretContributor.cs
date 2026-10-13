using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.Agents;

public sealed class ReadAppSecretContributor(IGitHubTokenMinter minter) : IRunSecretContributor
{
    private static readonly Dictionary<string, string> _readPermissions = new()
    {
        ["contents"] = "read",
        ["pull_requests"] = "read",
        ["checks"] = "read",
        ["statuses"] = "read",
        ["issues"] = "read",
        ["actions"] = "read",
    };

    public async Task<RunSecretContribution> ContributeAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Repo is null)
        {
            return new RunSecretContribution(new Dictionary<string, string>());
        }

        var token = await minter.MintTokenAsync(request.Repo.Repo, _readPermissions, cancellationToken).ConfigureAwait(false);
        return new RunSecretContribution(
            new Dictionary<string, string>
            {
                [AgentPodSpecBuilder.GitHubTokenKey] = token.Token,
                [AgentPodSpecBuilder.GitCredentialKey] = $"https://x-access-token:{token.Token}@github.com\n",
            },
            token.ExpiresAt);
    }
}
