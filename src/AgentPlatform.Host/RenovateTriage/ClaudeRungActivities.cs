using System.Net;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record QueueInput(string Repo, int PrNumber, string HeadSha, string Reason);

public sealed record QueueResult(bool HeadMoved, string Model, bool NotLive = false);

public sealed record ReadHeadInput(string Repo, int PrNumber);

public sealed class ClaudeRungActivities(IGitHubApiClient github, TriagePendingStore pendings, AgentPodsOptions pods, IOptions<RenovateTriageOptions> options)
{
    public const string QueueName = "RenovateTriageQueue";
    public const string ReadHeadName = "RenovateTriageReadHead";

    [Activity(QueueName)]
    public Task<QueueResult> QueueAsync(QueueInput input) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            Validate(input.Repo, input.PrNumber);
            if (options.Value.Mode != TriageMode.Live)
            {
                return new QueueResult(false, pods.Model, true);
            }

            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pr.HeadSha, input.HeadSha, StringComparison.Ordinal))
            {
                return new QueueResult(true, pods.Model);
            }

            // Row first: a label with no row would block the PR for good.
            await pendings.UpsertAsync(input.Repo, input.PrNumber, input.HeadSha, input.Reason, cancellationToken).ConfigureAwait(false);
            await github.AddLabelsAsync(input.Repo, input.PrNumber, [TriageRules.NeedsTriageLabel], cancellationToken).ConfigureAwait(false);
            var sticky = await TriageSticky.FindAsync(github, input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            await TriageSticky.UpsertAsync(github, input.Repo, input.PrNumber, sticky, StickyComment.Queued(input.Reason), cancellationToken).ConfigureAwait(false);
            return new QueueResult(false, pods.Model);
        });

    [Activity(ReadHeadName)]
    public Task<string> ReadHeadAsync(ReadHeadInput input) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            Validate(input.Repo, input.PrNumber);
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
            return pr.HeadSha;
        });

    private static void Validate(string repo, int prNumber)
    {
        GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
    }
}
