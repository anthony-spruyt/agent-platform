using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.Agents;

public sealed class ReadAppSecretContributor(IGitHubTokenMinter minter) : IRunSecretContributor
{
    public async Task<RunSecretContribution> ContributeAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Repo is null)
        {
            return new RunSecretContribution(new Dictionary<string, string>());
        }

        IReadOnlyDictionary<string, string> permissions = request.Overrides?.TokenPermissions ?? ReadTokenPermissions.Default;
        ReadTokenPermissions.EnsureNarrowsDefault(permissions);
        var token = await minter.MintTokenAsync(request.Repo.Repo, permissions, cancellationToken).ConfigureAwait(false);
        return new RunSecretContribution(
            new Dictionary<string, string>
            {
                [AgentPodSpecBuilder.GitHubTokenKey] = token.Token,
                [AgentPodSpecBuilder.GitCredentialKey] = $"https://x-access-token:{token.Token}@github.com\n",
            },
            token.ExpiresAt);
    }
}
