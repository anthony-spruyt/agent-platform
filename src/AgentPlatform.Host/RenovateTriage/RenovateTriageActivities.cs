using System.Net;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class RenovateTriageActivities(
    IGitHubApiClient github,
    IJevClient jev,
    TriageVerdictStore verdicts,
    IOptions<RenovateTriageOptions> options)
{
    public const string AssessName = "RenovateTriageAssess";
    public const string JudgeName = "RenovateTriageJudge";
    public const string RecordVerdictName = "RenovateTriageRecordVerdict";

    public const string NotOpen = "not-open";

    [Activity(AssessName)]
    public Task<AssessResult> AssessAsync(AssessInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            var sha = pr.HeadSha;

            var skip = pr.State != "open" || pr.Merged ? NotOpen : TriageRules.SkipReason(pr.AuthorId, pr.Labels);
            if (skip is not null)
            {
                return new AssessResult(AssessStatus.Skipped, sha, skip);
            }

            var reviews = await github.ListReviewsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (TriageRules.IsApproved(reviews))
            {
                return new AssessResult(AssessStatus.Skipped, sha, TriageRules.AlreadyApproved);
            }

            if (await verdicts.ExistsAsync(input.Repo, input.PrNumber, sha, cancellationToken).ConfigureAwait(false))
            {
                return new AssessResult(AssessStatus.AlreadyJudged, sha);
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, sha, cancellationToken).ConfigureAwait(false);
            return TriageRules.AssessChecks(checks) switch
            {
                CiState.Pending => new AssessResult(AssessStatus.Pending, sha),
                CiState.Failed => new AssessResult(AssessStatus.Decided, sha, Decision: TriageRules.CiFailedDecision()),
                _ => TriageRules.IsRiskyBranch(pr.HeadRef)
                    ? new AssessResult(AssessStatus.Decided, sha, Decision: TriageRules.HighRiskDecision())
                    : new AssessResult(AssessStatus.NeedsJev, sha),
            };
        });

    [Activity(JudgeName)]
    public Task<JudgeResult> JudgeAsync(JudgeInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pr.HeadSha, input.HeadSha, StringComparison.Ordinal))
            {
                return new JudgeResult(HeadMoved: true);
            }

            try
            {
                var answers = await jev.AskAsync(pr.Body, cancellationToken).ConfigureAwait(false);
                return new JudgeResult(false, TriageRules.DecideFromJev(pr.HeadRef, answers), answers);
            }
            catch (JevException ex)
            {
                return new JudgeResult(false, TriageRules.JevErrorDecision(), JevError: ex.Message);
            }
        });

    [Activity(RecordVerdictName)]
    public Task<RecordVerdictResult> RecordVerdictAsync(RecordVerdictInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var mode = options.Value.Mode;
            var inserted = await verdicts
                .RecordAsync(input.Repo, input.PrNumber, input.HeadSha, input.Decision, mode, ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);
            return new RecordVerdictResult(inserted, mode);
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
