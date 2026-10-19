using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed partial class RenovateTriageActivities(
    IGitHubApiClient github,
    IJevClient jev,
    TriageVerdictStore verdicts,
    TriagePendingStore pendings,
    IOptions<RenovateTriageOptions> options,
    IUntrustedTextScanner scanner)
{
    public const string AssessName = "RenovateTriageAssess";
    public const string JudgeName = "RenovateTriageJudge";
    public const string RecordVerdictName = "RenovateTriageRecordVerdict";
    public const string ApplyName = "RenovateTriageApply";
    public const string JevErrorFailureType = "JevError";
    public const string HeadMovedReason = "head-moved";
    public const string CiPendingReason = "ci-pending";

    private const string NotLive = "not-live";
    private const string DismissalMessage = "A newer commit needs review.";

    [Activity(AssessName)]
    public Task<AssessResult> AssessAsync(AssessInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var mode = options.Value.Mode;
            if (!TriageRules.IsAllowedOwner(input.Repo))
            {
                return new AssessResult(AssessStatus.Skipped, string.Empty, TriageRules.ForeignOwner, Mode: mode);
            }

            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            var sha = pr.HeadSha;

            var shadow = mode == TriageMode.Shadow;
            var pending = input.Claude && !shadow
                ? await pendings.FindAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false)
                : null;
            var hasOpenRow = pending is { Status: TriagePendingStatus.Pending or TriagePendingStatus.Redriven };
            var unapplied = mode == TriageMode.Live
                ? await verdicts.FindUnappliedAsync(input.Repo, input.PrNumber, sha, cancellationToken).ConfigureAwait(false)
                : null;
            var ignoreTriageSignals = shadow || unapplied is not null;
            var honourNeedsReviewLabel = unapplied is not null && TriageRules.IsApproving(unapplied.Verdict);
            var skip = SkipReason(input.Repo, pr, shadow, ignoreTriageSignals, honourNeedsReviewLabel, hasOpenRow);
            if (skip is not null)
            {
                return new AssessResult(AssessStatus.Skipped, sha, skip, Mode: mode);
            }

            var reviews = await github.ListReviewsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (TriageRules.IsApproved(reviews, sha, ignoreTriageSignals))
            {
                return new AssessResult(AssessStatus.Skipped, sha, TriageRules.AlreadyApproved, Mode: mode);
            }

            if (unapplied is not null)
            {
                return new AssessResult(AssessStatus.Decided, sha, Decision: unapplied, Mode: mode, Stored: true);
            }

            var rowIsForThisHead = hasOpenRow && string.Equals(pending!.HeadSha, sha, StringComparison.Ordinal);
            if (!rowIsForThisHead && await verdicts.ExistsAsync(input.Repo, input.PrNumber, sha, mode, cancellationToken).ConfigureAwait(false))
            {
                return new AssessResult(AssessStatus.AlreadyJudged, sha, Mode: mode);
            }

            var commits = await github.ListCommitsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (!await RenovateCommitCheck.IsRenovateOnlyAsync(github, input.Repo, pr, commits, cancellationToken).ConfigureAwait(false))
            {
                return new AssessResult(AssessStatus.Decided, sha, Decision: TriageRules.NonRenovateCommitDecision(), Mode: mode);
            }

            if (input.Step2 && !string.Equals(input.PrecheckedSha, sha, StringComparison.Ordinal))
            {
                return new AssessResult(AssessStatus.NeedsPrecheck, sha, Mode: mode);
            }

            if (rowIsForThisHead)
            {
                return new AssessResult(AssessStatus.Climb, sha, pending!.Reason, Mode: mode);
            }

            var checks = await github.ListCheckRunsAsync(input.Repo, sha, cancellationToken).ConfigureAwait(false);
            var suites = await github.ListCheckSuitesAsync(input.Repo, sha, cancellationToken).ConfigureAwait(false);
            var ci = TriageRules.AssessChecks(checks, suites);
            if (ci == CiState.Pending)
            {
                return new AssessResult(AssessStatus.Pending, sha, Mode: mode, StatusPending: TriageRules.HasPendingCommitStatus(checks));
            }

            if (ci == CiState.Failed)
            {
                var failed = TriageRules.FailedChecks(checks);
                var ciFailed = input.Step2 ? AssessStatus.CiFailed : AssessStatus.Decided;
                return new AssessResult(ciFailed, sha, Decision: TriageRules.CiFailedDecision(input.Repo, sha, failed), Mode: mode);
            }

            return TriageRules.RiskyTerm(pr.HeadRef) is { } term
                ? new AssessResult(AssessStatus.Decided, sha, Decision: TriageRules.HighRiskDecision(term), Mode: mode)
                : new AssessResult(AssessStatus.NeedsJev, sha, Mode: mode);
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
            var facts = BumpAnalyzer.Analyze(files);
            var jevBody = JevBody(input, pr.Body);

            JevAnswers answers;
            try
            {
                answers = await jev.AskAsync(jevBody, diff, cancellationToken).ConfigureAwait(false);
            }
            catch (JevException ex) when (input.RetryJevErrors)
            {
                throw new ApplicationFailureException(ex.Message, ex, JevErrorFailureType);
            }
            catch (JevException ex)
            {
                return new JudgeResult(false, TriageRules.JevErrorDecision(), JevError: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
            }

            return new JudgeResult(false, TriageRules.DecideFromJev(pr.HeadRef, answers, facts), answers);
        });

    [Activity(RecordVerdictName)]
    public Task<RecordVerdictResult> RecordVerdictAsync(RecordVerdictInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            var inserted = await verdicts
                .RecordAsync(input.Repo, input.PrNumber, input.HeadSha, input.Decision, input.Mode, input.Rung, ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);
            return new RecordVerdictResult(inserted, input.Mode);
        });

    [Activity(ApplyName)]
    public Task<ApplyResult> ApplyAsync(ApplyInput input) =>
        GuardAsync(async () =>
        {
            Validate(input.Repo, input.PrNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.HeadSha);
            ArgumentNullException.ThrowIfNull(input.Decision);
            if (!TriageRules.IsAllowedOwner(input.Repo))
            {
                return new ApplyResult(ApplyOutcome.Refused, TriageRules.ForeignOwner);
            }

            if (input.Mode != TriageMode.Live || options.Value.Mode != TriageMode.Live)
            {
                return new ApplyResult(ApplyOutcome.Refused, NotLive);
            }

            var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
            var logger = ActivityExecutionContext.Current.Logger;
            var pr = await github.GetPullRequestAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
            if (pr.State != "open" || pr.Merged)
            {
                LogNotApplied(logger, input.Repo, input.PrNumber, TriageRules.NotOpen);
                return new ApplyResult(ApplyOutcome.Aborted, TriageRules.NotOpen);
            }

            if (!string.Equals(pr.HeadSha, input.HeadSha, StringComparison.Ordinal))
            {
                LogNotApplied(logger, input.Repo, input.PrNumber, HeadMovedReason);
                return new ApplyResult(ApplyOutcome.Aborted, HeadMovedReason);
            }

            var gate = TriageRules.IsApproving(input.Decision.Verdict)
                ? await GateApprovalAsync(input, pr, cancellationToken).ConfigureAwait(false)
                : GateFlag(input, pr, input.Decision);
            if (gate.Stop is { } stop)
            {
                LogNotApplied(logger, input.Repo, input.PrNumber, stop.Reason);
                return stop;
            }

            var action = await WriteEffectAsync(input, gate, cancellationToken).ConfigureAwait(false);
            if (pr.Labels.Contains(TriageRules.NeedsTriageLabel, StringComparer.Ordinal))
            {
                await github.RemoveLabelAsync(input.Repo, input.PrNumber, TriageRules.NeedsTriageLabel, cancellationToken).ConfigureAwait(false);
            }

            var handedToOwner = gate.Decision.Verdict is TriageVerdict.Owner or TriageVerdict.NeedsReview;
            await pendings.SetStatusAsync(input.Repo, input.PrNumber, input.HeadSha, handedToOwner ? TriagePendingStatus.Owner : TriagePendingStatus.Settled, cancellationToken).ConfigureAwait(false);
            await pendings.SettleOtherHeadsAsync(input.Repo, input.PrNumber, input.HeadSha, cancellationToken).ConfigureAwait(false);
            if (gate.Decision == input.Decision)
            {
                await verdicts.MarkAppliedAsync(input.Repo, input.PrNumber, input.HeadSha, input.Decision.Verdict, action, cancellationToken).ConfigureAwait(false);
                return new ApplyResult(ApplyOutcome.Applied);
            }

            await verdicts.MarkAppliedAsAsync(input.Repo, input.PrNumber, input.HeadSha, input.Decision.Verdict, gate.Decision, action, cancellationToken).ConfigureAwait(false);
            return new ApplyResult(ApplyOutcome.Applied, Effective: gate.Decision);
        });

    private sealed record Gate(TriageDecision Decision, ApplyResult? Stop = null, string? HighRiskTerm = null);

    // A flag is not held to the approval gates, but the owner's own labels still stop it; the label it writes itself does not, so a retry completes.
    private static Gate GateFlag(ApplyInput input, GitHubPullRequest pr, TriageDecision decision)
    {
        var own = FlagLabel(decision.Verdict);
        var others = pr with { Labels = [.. pr.Labels.Where(l => !string.Equals(l, own, StringComparison.Ordinal))] };
        var skip = SkipReason(input.Repo, others, shadow: false, ignoreTriageSignals: true, honourNeedsReviewLabel: true);
        return skip is null ? new Gate(decision) : new Gate(decision, new ApplyResult(ApplyOutcome.Refused, skip));
    }

    // An approving verdict is only acted on after the checks Assess ran are redone on fresh data at this head.
    // The needs-review label is judged on the outcome: a hand-off writes it itself, so a retry must get past it.
    private async Task<Gate> GateApprovalAsync(ApplyInput input, GitHubPullRequest pr, CancellationToken cancellationToken)
    {
        var skip = SkipReason(input.Repo, pr, shadow: false, ignoreTriageSignals: true, honourNeedsReviewLabel: false);
        if (skip is not null)
        {
            return new Gate(input.Decision, new ApplyResult(ApplyOutcome.Refused, skip));
        }

        var gate = await DecideApprovalAsync(input, pr, cancellationToken).ConfigureAwait(false);
        if (gate.Stop is not null)
        {
            return gate;
        }

        if (!TriageRules.IsApproving(gate.Decision.Verdict))
        {
            return GateFlag(input, pr, gate.Decision);
        }

        skip = SkipReason(input.Repo, pr, shadow: false, ignoreTriageSignals: true, honourNeedsReviewLabel: true);
        return skip is null ? gate : new Gate(gate.Decision, new ApplyResult(ApplyOutcome.Refused, skip));
    }

    private async Task<Gate> DecideApprovalAsync(ApplyInput input, GitHubPullRequest pr, CancellationToken cancellationToken)
    {
        var commits = await github.ListCommitsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
        if (!await RenovateCommitCheck.IsRenovateOnlyAsync(github, input.Repo, pr, commits, cancellationToken).ConfigureAwait(false))
        {
            return new Gate(TriageRules.NonRenovateCommitDecision());
        }

        var precheck = await TriagePrecheck.RunAsync(github, scanner, input.Repo, input.PrNumber, pr, cancellationToken).ConfigureAwait(false);
        if (precheck.HeadMoved)
        {
            return new Gate(input.Decision, new ApplyResult(ApplyOutcome.Aborted, HeadMovedReason));
        }

        if (precheck.Verdict != PrecheckVerdict.Passed)
        {
            return new Gate(TriageRules.PrecheckDecision(precheck.Verdict));
        }

        return input.Decision.Verdict == TriageVerdict.Safe
            ? await GateSafeAsync(input, pr, cancellationToken).ConfigureAwait(false)
            : new Gate(input.Decision);
    }

    private async Task<Gate> GateSafeAsync(ApplyInput input, GitHubPullRequest pr, CancellationToken cancellationToken)
    {
        var checks = await github.ListCheckRunsAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
        var suites = await github.ListCheckSuitesAsync(input.Repo, input.HeadSha, cancellationToken).ConfigureAwait(false);
        var ci = TriageRules.AssessChecks(checks, suites);
        return TriageRules.AssessSafe(ci, pr.HeadRef) switch
        {
            SafeAction.Fixable => new Gate(TriageRules.SafeOnRedHeadDecision(input.Repo, input.HeadSha, TriageRules.FailedChecks(checks))),
            SafeAction.WaitForCi when input.CiWaitOver => new Gate(TriageRules.CiTimeoutDecision()),
            SafeAction.WaitForCi => new Gate(input.Decision, new ApplyResult(ApplyOutcome.Aborted, CiPendingReason, StatusPending: TriageRules.HasPendingCommitStatus(checks))),
            SafeAction.CommentOnly => new Gate(input.Decision, HighRiskTerm: TriageRules.RiskyTerm(pr.HeadRef)),
            _ => new Gate(input.Decision),
        };
    }

    private async Task<VerdictAction> WriteEffectAsync(ApplyInput input, Gate gate, CancellationToken cancellationToken)
    {
        var decision = gate.Decision;
        switch (decision.Verdict)
        {
            case TriageVerdict.Approve:
            case TriageVerdict.Safe when gate.HighRiskTerm is null:
                await ApproveAsync(input, decision, cancellationToken).ConfigureAwait(false);
                return VerdictAction.Approve;
            case TriageVerdict.Safe:
                await FlagAsync(input, decision with { Comment = TriageRules.HighRiskSafeComment(gate.HighRiskTerm, decision.Comment) }, TriageRules.SafeLabel, cancellationToken).ConfigureAwait(false);
                return VerdictAction.Comment;
            default:
                await FlagAsync(input, decision, FlagLabel(decision.Verdict), cancellationToken).ConfigureAwait(false);
                return VerdictAction.Comment;
        }
    }

    private static string FlagLabel(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.Fixable => TriageRules.FixableLabel,
        TriageVerdict.Blocked => TriageRules.BlockedLabel,
        _ => TriageRules.NeedsReviewLabel,
    };

    private async Task ApproveAsync(ApplyInput input, TriageDecision decision, CancellationToken cancellationToken)
    {
        var safe = decision.Verdict == TriageVerdict.Safe;
        var summary = safe ? TriageRules.SafeApprovalComment : decision.Comment ?? "**Auto-approved:**";
        var reviews = await github.ListReviewsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
        if (!reviews.Any(r => IsBotApproval(r) && string.Equals(r.CommitId, input.HeadSha, StringComparison.Ordinal)))
        {
            await github.ApproveAsync(input.Repo, input.PrNumber, input.HeadSha, summary, cancellationToken).ConfigureAwait(false);
        }

        await github.SetExclusiveLabelAsync(input.Repo, input.PrNumber, TriageRules.VerdictLabels, safe ? TriageRules.SafeLabel : TriageRules.AutoApprovedLabel, cancellationToken).ConfigureAwait(false);

        var sticky = await FindStickyAsync(input, cancellationToken).ConfigureAwait(false);
        if (safe)
        {
            if (decision.Comment is { } comment)
            {
                await UpsertStickyAsync(input, sticky, comment, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (sticky is not null)
        {
            await UpsertStickyAsync(input, sticky, TriageRules.StickyApprovalText(input.HeadSha, decision.Comment), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlagAsync(ApplyInput input, TriageDecision decision, string label, CancellationToken cancellationToken)
    {
        var reviews = await github.ListReviewsAsync(input.Repo, input.PrNumber, cancellationToken).ConfigureAwait(false);
        foreach (var stale in reviews.Where(IsBotApproval))
        {
            await DismissAsync(input, stale, cancellationToken).ConfigureAwait(false);
        }

        await github.SetExclusiveLabelAsync(input.Repo, input.PrNumber, TriageRules.VerdictLabels, label, cancellationToken).ConfigureAwait(false);

        if (decision.Comment is { } comment)
        {
            var sticky = await FindStickyAsync(input, cancellationToken).ConfigureAwait(false);
            await UpsertStickyAsync(input, sticky, comment, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DismissAsync(ApplyInput input, GitHubReview review, CancellationToken cancellationToken)
    {
        try
        {
            await github.DismissReviewAsync(input.Repo, input.PrNumber, review.Id, DismissalMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            LogDismissRefused(ActivityExecutionContext.Current.Logger, review.Id, input.Repo, input.PrNumber);
        }
    }

    private Task<GitHubIssueComment?> FindStickyAsync(ApplyInput input, CancellationToken cancellationToken) =>
        TriageSticky.FindAsync(github, input.Repo, input.PrNumber, cancellationToken);

    private Task UpsertStickyAsync(ApplyInput input, GitHubIssueComment? sticky, string text, CancellationToken cancellationToken) =>
        TriageSticky.UpsertAsync(github, input.Repo, input.PrNumber, sticky, text, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not applying the verdict for {Repo}#{Number}: {Reason}.")]
    private static partial void LogNotApplied(ILogger logger, string repo, int number, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub refused to dismiss review {ReviewId} on {Repo}#{Number}; carrying on.")]
    private static partial void LogDismissRefused(ILogger logger, long reviewId, string repo, int number);

    private static bool IsBotApproval(GitHubReview review) =>
        string.Equals(review.AuthorLogin, TriageRules.TriageBotLogin, StringComparison.Ordinal)
        && string.Equals(review.State, "APPROVED", StringComparison.OrdinalIgnoreCase);

    private static string? SkipReason(string repo, GitHubPullRequest pr, bool shadow, bool ignoreTriageSignals, bool honourNeedsReviewLabel, bool hasOpenRow = false)
    {
        if (TriageRules.IsNotOpen(pr.State, pr.Merged, shadow))
        {
            return TriageRules.NotOpen;
        }

        return TriageRules.SkipReason(pr.AuthorId, pr.Labels, ignoreTriageSignals, honourNeedsReviewLabel, hasOpenRow)
            ?? (TriageRules.IsSameRepo(repo, pr.HeadRepo, pr.BaseRepo) ? null : TriageRules.ForeignRepo);
    }

    private static string? JevBody(JudgeInput input, string? liveBody)
    {
        if (input.Body is null)
        {
            return liveBody;
        }

        var texts = new List<ScannedText> { input.Body };
        texts.AddRange(input.Changelogs ?? []);
        if (texts.Exists(t => !t.IsIntact))
        {
            throw new ArgumentException("A scanned text no longer matches the hash it was scanned under.");
        }

        return string.Join("\n\n---\n\n", texts.Select(t => t.Text));
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
