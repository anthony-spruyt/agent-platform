using System.Collections.Frozen;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

public sealed partial class LivemapCheckSuiteHandler(
    IOptions<LivemapOptions> options,
    ILivemapWakeStarter wakes,
    ILogger<LivemapCheckSuiteHandler> logger) : IGitHubEventHandler
{
    private const string MainBranch = "main";

    private const string ActionsAppSlug = "github-actions";

    public IReadOnlySet<string> AcceptedEvents { get; } = new[] { "check_suite" }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<GitHubEventOutcome> HandleAsync(GitHubWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);

        if (!options.Value.Enabled)
        {
            return GitHubEventOutcome.Ignored;
        }

        CheckSuiteEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CheckSuiteEvent>(webhookEvent.Body.Span, GitHubJson.Options);
        }
        catch (JsonException)
        {
            return GitHubEventOutcome.Malformed;
        }

        if (payload is not { Action: "completed", CheckSuite: { Id: > 0, HeadSha.Length: > 0 } suite, Repository: { Id: > 0, FullName: { } fullName } repository }
            || !string.Equals(fullName, options.Value.Repo, StringComparison.OrdinalIgnoreCase)
            || !IsBuildRun(suite, repository.Id))
        {
            return GitHubEventOutcome.Ignored;
        }

        await wakes.StartAsync(new LivemapBuildWake(suite.HeadSha!, suite.Id), cancellationToken).ConfigureAwait(false);
        LogWoken(logger, suite.Id, webhookEvent.DeliveryId.Length > 0 ? webhookEvent.DeliveryId : "unknown");
        return GitHubEventOutcome.Accepted;
    }

    private static bool IsBuildRun(CheckSuiteEvent.Suite suite, long repositoryId) =>
        suite.App?.Slug == ActionsAppSlug
        && (suite.PullRequests is { Count: > 0 } pullRequests
            ? LivemapBuildBranch.IsMatch(suite.HeadBranch) && pullRequests.All(pullRequest => IsInRepository(pullRequest, repositoryId))
            : suite.HeadBranch == MainBranch);

    private static bool IsInRepository(CheckSuiteEvent.PullRequestInfo pullRequest, long repositoryId) =>
        pullRequest is { Head.Repo: { } head, Base.Repo: { } @base } && head.Id == repositoryId && @base.Id == repositoryId;

    [LoggerMessage(Level = LogLevel.Information, Message = "Woke the Livemap build for check suite {SuiteId} from delivery {DeliveryId}.")]
    private static partial void LogWoken(ILogger logger, long suiteId, string deliveryId);
}
