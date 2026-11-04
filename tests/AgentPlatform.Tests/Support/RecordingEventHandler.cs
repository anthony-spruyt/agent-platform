using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Tests.Support;

internal sealed class RecordingEventHandler(params string[] acceptedEvents) : IGitHubEventHandler
{
    public IReadOnlySet<string> AcceptedEvents { get; } = new HashSet<string>(acceptedEvents, StringComparer.Ordinal);

    public GitHubEventOutcome Outcome { get; set; } = GitHubEventOutcome.Accepted;

    public Exception? Failure { get; set; }

    public List<GitHubWebhookEvent> Received { get; } = [];

    public Task<GitHubEventOutcome> HandleAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        lock (Received)
        {
            Received.Add(webhookEvent);
        }

        return Failure is null ? Task.FromResult(Outcome) : Task.FromException<GitHubEventOutcome>(Failure);
    }
}
