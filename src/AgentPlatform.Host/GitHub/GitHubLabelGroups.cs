namespace AgentPlatform.Host.GitHub;

public static class GitHubLabelGroups
{
    // Removes the group's labels that are on the item, except the one to keep, then adds it; null only clears the group.
    public static async Task SetExclusiveLabelAsync(this IGitHubApiClient github, string repo, int number, IReadOnlyCollection<string> group, string? label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(group);

        var present = await github.ListLabelsAsync(repo, number, cancellationToken).ConfigureAwait(false);
        foreach (var stale in group.Where(g => !string.Equals(g, label, StringComparison.Ordinal) && present.Contains(g, StringComparer.Ordinal)))
        {
            await github.RemoveLabelAsync(repo, number, stale, cancellationToken).ConfigureAwait(false);
        }

        if (label is not null)
        {
            await github.AddLabelsAsync(repo, number, [label], cancellationToken).ConfigureAwait(false);
        }
    }
}
