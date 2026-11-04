using System.Collections.Frozen;
using System.Text.Json;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.GitHub;

public sealed partial class RenovateTriageCheckSuiteHandler(
    IRenovateTriageSignaler signaler,
    IOptions<GitHubWebhookOptions> options,
    ILogger<GitHubWebhookLog> logger) : IGitHubEventHandler
{
    public IReadOnlySet<string> AcceptedEvents { get; } = new[] { "check_suite" }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<GitHubEventOutcome> HandleAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);

        IReadOnlyList<CheckSuiteCompletedSignal> signals;
        try
        {
            signals = CheckSuiteEvent.ToSignals(webhookEvent.Body.Span, webhookEvent.DeliveryId, GitHubAppOptions.RequireAppId(options.Value.AppId));
        }
        catch (JsonException)
        {
            return GitHubEventOutcome.Malformed;
        }

        var loggedDelivery = webhookEvent.DeliveryId.Length > 0 ? webhookEvent.DeliveryId : "unknown";
        foreach (var signal in signals)
        {
            await signaler.SignalAsync(signal, cancellationToken).ConfigureAwait(false);
            LogForwarded(logger, signal.Repo, signal.PrNumber, loggedDelivery);
        }

        return signals.Count > 0 ? GitHubEventOutcome.Accepted : GitHubEventOutcome.Ignored;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Forwarded check_suite completion for {Repo}#{PrNumber} from delivery {DeliveryId}.")]
    private static partial void LogForwarded(ILogger logger, string repo, int prNumber, string deliveryId);
}
