using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

internal static class TriageSticky
{
    public static async Task<GitHubIssueComment?> FindAsync(IGitHubApiClient github, string repo, int prNumber, CancellationToken cancellationToken)
    {
        var comments = await github.ListIssueCommentsAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        return comments.FirstOrDefault(c => string.Equals(c.AuthorLogin, TriageRules.TriageBotLogin, StringComparison.Ordinal)
            && c.Body.Contains(TriageRules.StickyMarker, StringComparison.Ordinal));
    }

    public static async Task UpsertAsync(IGitHubApiClient github, string repo, int prNumber, GitHubIssueComment? sticky, string text, CancellationToken cancellationToken)
    {
        var body = $"{TriageRules.StickyMarker}\n{text}";
        if (sticky is null)
        {
            await github.CreateIssueCommentAsync(repo, prNumber, body, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(sticky.Body, body, StringComparison.Ordinal))
        {
            await github.UpdateIssueCommentAsync(repo, sticky.Id, body, cancellationToken).ConfigureAwait(false);
        }
    }
}
