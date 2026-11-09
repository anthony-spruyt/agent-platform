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
    private const string BuildBranchPrefix = "livemap/build-";

    private const string MainBranch = "main";

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
        suite.PullRequests is { Count: > 0 } pullRequests
            ? IsBuildBranch(suite.HeadBranch) && pullRequests.All(pullRequest => IsInRepository(pullRequest, repositoryId))
            : suite.HeadBranch is null or MainBranch;

    private static bool IsInRepository(CheckSuiteEvent.PullRequestInfo pullRequest, long repositoryId) =>
        pullRequest is { Head.Repo: { } head, Base.Repo: { } @base } && head.Id == repositoryId && @base.Id == repositoryId;

    private static bool IsBuildBranch(string? branch) =>
        branch is not null
        && branch.StartsWith(BuildBranchPrefix, StringComparison.Ordinal)
        && branch.Length > BuildBranchPrefix.Length
        && branch.AsSpan(BuildBranchPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    [LoggerMessage(Level = LogLevel.Information, Message = "Woke the Livemap build for check suite {SuiteId} from delivery {DeliveryId}.")]
    private static partial void LogWoken(ILogger logger, long suiteId, string deliveryId);
}
