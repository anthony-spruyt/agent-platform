using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

public static class OwnerHandling
{
    // Only the owner counts: the platform's own label changes and other reviewers never take a PR out of the queue.
    public static bool IsHandled(DateTimeOffset since, IReadOnlyList<GitHubReview> reviews, IReadOnlyList<GitHubIssueEvent> events)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(events);

        return reviews.Any(r => IsOwner(r.AuthorLogin) && r.SubmittedAt >= since)
            || events.Any(e => IsOwner(e.ActorLogin) && e.CreatedAt >= since && TakesOver(e));
    }

    private static bool IsOwner(string? login) => string.Equals(login, TriageRules.AllowedOwner, StringComparison.OrdinalIgnoreCase);

    private static bool TakesOver(GitHubIssueEvent change) => change.Event switch
    {
        "unlabeled" => change.Label is TriageRules.NeedsReviewLabel or TriageRules.NeedsTriageLabel,
        "labeled" => change.Label is TriageRules.BlockedLabel or TriageRules.RevertLabel,
        _ => false,
    };
}
