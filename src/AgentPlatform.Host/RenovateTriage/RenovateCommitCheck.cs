using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

internal static class RenovateCommitCheck
{
    public const int MaxMerges = 10;

    private static readonly string[] _movedStatuses = ["renamed", "copied"];

    // Don't catch GitHub errors here: a transient one must throw so the activity retries instead of recording a verdict.
    public static async Task<bool> IsRenovateOnlyAsync(
        IGitHubApiClient github,
        string repo,
        GitHubPullRequest pr,
        IReadOnlyList<GitHubCommit> commits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(commits);
        if (TriageRules.AreAllRenovateCommits(commits, pr.Commits))
        {
            return true;
        }

        return ChainOfMerges(pr, commits) is { } chain
            && await MergesAddOnlyTheBaseAsync(github, repo, pr, chain, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Chain(GitHubCommit Renovate, IReadOnlyList<string> BaseParents);

    private static Chain? ChainOfMerges(GitHubPullRequest pr, IReadOnlyList<GitHubCommit> commits)
    {
        if (pr.Commits is not (> 0 and < TriageRules.MaxListedCommits)
            || commits.Count != pr.Commits
            || !string.Equals(commits[^1].Sha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var renovates = TriageRules.LeadingRenovateCommits(commits);
        if (renovates == 0 || renovates == commits.Count || commits.Count - renovates > MaxMerges)
        {
            return null;
        }

        for (var i = 0; i < renovates; i++)
        {
            if (!HasParents(commits[i], i == 0 ? null : commits[i - 1], 1))
            {
                return null;
            }
        }

        var baseParents = new List<string>();
        for (var i = renovates; i < commits.Count; i++)
        {
            if (TriageRules.IsRenovateCommit(commits[i]) || !HasParents(commits[i], commits[i - 1], 2))
            {
                return null;
            }

            baseParents.Add(commits[i].Parents![1]);
        }

        return new Chain(commits[renovates - 1], baseParents);
    }

    private static bool HasParents(GitHubCommit commit, GitHubCommit? previous, int count) =>
        commit.Parents is { } parents
        && parents.Count == count
        && (previous is null || string.Equals(parents[0], previous.Sha, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> MergesAddOnlyTheBaseAsync(IGitHubApiClient github, string repo, GitHubPullRequest pr, Chain chain, CancellationToken cancellationToken)
    {
        if (pr.BaseRef.Length == 0)
        {
            return false;
        }

        foreach (var parent in chain.BaseParents)
        {
            var onBase = await github.CompareAsync(repo, pr.BaseRef, parent, cancellationToken).ConfigureAwait(false);
            if (onBase?.Status is not ("behind" or "identical"))
            {
                return false;
            }
        }

        var branched = await github.CompareAsync(repo, pr.BaseRef, chain.Renovate.Sha, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branched?.MergeBaseSha))
        {
            return false;
        }

        var added = await github.CompareAsync(repo, chain.Renovate.Sha, pr.HeadSha, cancellationToken).ConfigureAwait(false);
        var baseChange = await github.CompareAsync(repo, branched.MergeBaseSha, chain.BaseParents[^1], cancellationToken).ConfigureAwait(false);
        return added?.Status is "ahead" or "identical"
            && baseChange is not null
            && SameFiles(added.Files, baseChange.Files);
    }

    private static bool SameFiles(IReadOnlyList<GitHubCompareFile> added, IReadOnlyList<GitHubCompareFile> baseChange)
    {
        if (added.Count >= GitHubApiClient.CompareFileLimit
            || baseChange.Count >= GitHubApiClient.CompareFileLimit
            || added.Concat(baseChange).Any(f => string.IsNullOrEmpty(f.Sha) || _movedStatuses.Contains(f.Status, StringComparer.Ordinal)))
        {
            return false;
        }

        return Keys(added).SequenceEqual(Keys(baseChange), StringComparer.Ordinal);
    }

    private static IEnumerable<string> Keys(IReadOnlyList<GitHubCompareFile> files) =>
        files.Select(f => $"{f.Filename}\n{f.PreviousFilename}\n{f.Status}\n{f.Sha}").Order(StringComparer.Ordinal);
}
