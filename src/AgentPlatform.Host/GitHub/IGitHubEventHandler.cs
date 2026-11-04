namespace AgentPlatform.Host.GitHub;

// Declared weakest to strongest: the router answers with the strongest outcome any handler reported
public enum GitHubEventOutcome
{
    Ignored,
    Accepted,
    Malformed,
}

// DeliveryId is the delivery GUID, or empty when the header was not one, so it is safe to log
public sealed record GitHubWebhookEvent(string Name, string DeliveryId, ReadOnlyMemory<byte> Body);

#pragma warning disable CA1711 // A webhook handler, not a .NET event handler
public interface IGitHubEventHandler
#pragma warning restore CA1711
{
    IReadOnlySet<string> AcceptedEvents { get; }

    // A failed delivery is redelivered whole and runs every handler of the event again, so this must be idempotent
    Task<GitHubEventOutcome> HandleAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken);
}
