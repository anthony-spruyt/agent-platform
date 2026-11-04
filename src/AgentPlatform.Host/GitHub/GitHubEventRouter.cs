using System.Collections.Frozen;
using System.Runtime.ExceptionServices;

namespace AgentPlatform.Host.GitHub;

public interface IGitHubEventRouter
{
    IReadOnlySet<string> AcceptedEvents { get; }

    Task<GitHubEventOutcome> RouteAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken);
}

public sealed partial class GitHubEventRouter : IGitHubEventRouter
{
    private readonly FrozenDictionary<string, IGitHubEventHandler[]> _handlersByEvent;
    private readonly ILogger<GitHubWebhookLog> _logger;

    public GitHubEventRouter(IEnumerable<IGitHubEventHandler> handlers, ILogger<GitHubWebhookLog> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _handlersByEvent = handlers
            .SelectMany(handler => handler.AcceptedEvents.Select(name => (Name: name, Handler: handler)))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Handler).ToArray(), StringComparer.Ordinal);
        AcceptedEvents = _handlersByEvent.Keys.ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> AcceptedEvents { get; }

    // Sequential in registration order, so a slow handler delays the ones after it and the answer to GitHub
    public async Task<GitHubEventOutcome> RouteAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);

        if (!_handlersByEvent.TryGetValue(webhookEvent.Name, out var handlers))
        {
            return GitHubEventOutcome.Ignored;
        }

        var outcome = GitHubEventOutcome.Ignored;
        List<Exception>? failures = null;
        foreach (var handler in handlers)
        {
            try
            {
                var handled = await handler.HandleAsync(webhookEvent, cancellationToken).ConfigureAwait(false);
                if (handled > outcome)
                {
                    outcome = handled;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, ex, handler.GetType().Name, webhookEvent.Name, webhookEvent.DeliveryId.Length > 0 ? webhookEvent.DeliveryId : "unknown");
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        return failures is null ? outcome : throw new AggregateException(failures);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub webhook delivery {DeliveryId} ({EventName}) failed in handler {Handler}.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handler, string eventName, string deliveryId);
}
