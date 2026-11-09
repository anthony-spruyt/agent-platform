namespace AgentPlatform.Host.Livemap;

internal static class LivemapBranches
{
    // A branch the worker owns starts each run clean, so an attempt that died half way is repeated whole.
    public static async Task RemoveAsync(ILivemapPullRequests repository, string branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        foreach (var pull in await repository.FindOpenByBranchAsync(branch, cancellationToken).ConfigureAwait(false))
        {
            await repository.ClosePullRequestAsync(pull.Number, cancellationToken).ConfigureAwait(false);
        }

        await repository.DeleteBranchAsync(branch, cancellationToken).ConfigureAwait(false);
    }
}
