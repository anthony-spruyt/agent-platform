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

    [Activity(AssessName)]
    public Task<AssessResult> AssessAsync(AssessInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            var sha = pr.HeadSha;

            var skip = SkipReason(input.Repo, pr);
            if (skip is not null)
            {
                return new AssessResult(AssessStatus.Skipped, sha, skip);
            }

            var reviews = await github.ListReviewsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (TriageRules.IsApproved(reviews, sha))
            {
                return new AssessResult(AssessStatus.Skipped, sha, TriageRules.AlreadyApproved);
            }

            if (await verdicts.ExistsAsync(input.Repo, input.PrNumber, sha, cancellationToken).ConfigureAwait(false))
            {
                return new AssessResult(AssessStatus.AlreadyJudged, sha);
            }

            var commits = await github.ListCommitsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!TriageRules.AreAllRenovateCommits(commits, pr.Commits))
            {
                return new AssessResult(AssessStatus.Decided, sha, Decision: TriageRules.NonRenovateCommitDecision());
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, sha, cancellationToken).ConfigureAwait(false);
            var suites = await github.ListCheckSuitesAsync(input.Repo, sha, cancellationToken).ConfigureAwait(false);
            return TriageRules.AssessChecks(checks, suites) switch
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

            var files = await github.ListPullRequestFilesAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            var diff = PrDiff.Build(files, JevClient.MaxPrDiffBytes);

            JevAnswers answers;
            try
            {
                answers = await jev.AskAsync(pr.Body, diff, cancellationToken).ConfigureAwait(false);
            }
            catch (JevException ex)
            {
                return new JudgeResult(false, TriageRules.JevErrorDecision(), JevError: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
            }

            return new JudgeResult(false, TriageRules.DecideFromJev(pr.HeadRef, answers), answers);
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

    private static string? SkipReason(string repo, GitHubPullRequest pr)
    {
        if (pr.State != "open" || pr.Merged)
        {
            return TriageRules.NotOpen;
        }

        return TriageRules.SkipReason(pr.AuthorId, pr.Labels)
            ?? (TriageRules.IsSameRepo(repo, pr.HeadRepo, pr.BaseRepo) ? null : TriageRules.ForeignRepo);
    }

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
