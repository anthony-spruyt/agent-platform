using System.Text.Json;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.GitHub;

public static partial class GitHubWebhookEndpoint
{
    public const string Path = "/webhooks/github";
    public const int MaxBodyBytes = 1024 * 1024;

    private const string EventHeader = "X-GitHub-Event";
    private const string DeliveryHeader = "X-GitHub-Delivery";

    public static IEndpointRouteBuilder MapGitHubWebhook(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(Path, HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IOptions<GitHubWebhookOptions> options,
        IRenovateTriageSignaler signaler,
        ILogger<GitHubWebhookLog> logger,
        CancellationToken cancellationToken)
    {
        var delivery = request.Headers[DeliveryHeader].ToString();
        var validDelivery = Guid.TryParse(delivery, out var deliveryGuid);
        var loggedDelivery = validDelivery ? deliveryGuid.ToString() : "unknown";
        var signature = request.Headers[WebhookSignature.HeaderName].ToString();
        if (signature.Length == 0)
        {
            LogRejected(logger, loggedDelivery);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var body = await ReadBodyAsync(request.Body, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            LogTooLarge(logger, loggedDelivery);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!WebhookSignature.IsValid(body, signature, options.Value.WebhookSecret))
        {
            LogRejected(logger, loggedDelivery);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var eventName = request.Headers[EventHeader].ToString();
        if (eventName != "check_suite")
        {
            return Results.Ok();
        }

        IReadOnlyList<CheckSuiteCompletedSignal> signals;
        try
        {
            signals = CheckSuiteEvent.ToSignals(body, validDelivery ? loggedDelivery : string.Empty);
        }
        catch (JsonException)
        {
            LogMalformed(logger, loggedDelivery);
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        foreach (var signal in signals)
        {
            await signaler.SignalAsync(signal, cancellationToken).ConfigureAwait(false);
            LogForwarded(logger, signal.Repo, signal.PrNumber, loggedDelivery);
        }

        return signals.Count > 0 ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Ok();
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "GitHub webhook delivery {DeliveryId} rejected: signature missing or invalid.")]
    private static partial void LogRejected(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GitHub webhook delivery {DeliveryId} rejected: body over the size limit.")]
    private static partial void LogTooLarge(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub webhook delivery {DeliveryId} has a malformed check_suite body.")]
    private static partial void LogMalformed(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forwarded check_suite completion for {Repo}#{PrNumber} from delivery {DeliveryId}.")]
    private static partial void LogForwarded(ILogger logger, string repo, int prNumber, string deliveryId);
}

public sealed class GitHubWebhookLog;
