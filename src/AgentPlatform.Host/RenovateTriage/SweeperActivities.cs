using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record RedriveResult(int Open, int Running, int Settled, int Redriven, int Errors, int Skipped);

public sealed record RedeliverResult(int Pages, int Planned, int Redelivered, int Refused);

public sealed record ReconcileResult(int Repos, int Signalled, int Failed, int Errors);

public sealed partial class SweeperActivities(
    IGitHubApiClient github,
    IGitHubAppClient app,
    TriagePendingStore pendings,
    ClaudeBudgetStore budget,
    TriageVerdictStore verdicts,
    SweeperStore state,
    IRenovateTriageSignaler signaler,
    ITriageWorkflowProbe probe,
    IOptions<RenovateTriageOptions> options,
    TimeProvider time,
    ILogger<SweeperActivities> logger)
{
    public const string RedriveName = "SweeperRedrive";
    public const string RedeliverName = "SweeperRedeliver";
    public const string ReconcileName = "SweeperReconcile";
    public const string RecordSuccessName = "SweeperRecordSuccess";

    public static readonly TimeSpan Lookback = TimeSpan.FromDays(3);
    public static readonly TimeSpan DeliveryOverlap = TimeSpan.FromMinutes(10);

    public const int MaxDeliveryPages = 50;

    private const string SweeperSource = "sweeper";

    [Activity(RedriveName)]
    public async Task<RedriveResult> RedriveAsync()
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var open = (await pendings.ListOpenAsync(cancellationToken).ConfigureAwait(false)).Where(r => TriageRules.IsAllowedOwner(r.Repo)).ToList();
        if (open.Count == 0 || options.Value.Mode != TriageMode.Live)
        {
            return new RedriveResult(open.Count, 0, 0, 0, 0, 0);
        }

        int running = 0, settled = 0, skipped = 0, errors = 0;
        var candidates = new List<(TriagePending Row, GitHubPullRequest Pr)>();
        foreach (var row in open)
        {
            try
            {
                if (await probe.IsRunningAsync(row.Repo, row.PrNumber, cancellationToken).ConfigureAwait(false))
                {
                    running++;
                    continue;
                }

                var (state, pr) = await AssessRowAsync(row, cancellationToken).ConfigureAwait(false);
                switch (state)
                {
                    case RowState.Redrive:
                        candidates.Add((row, pr));
                        break;
                    case RowState.Settled:
                        settled++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (GitHubApiException ex)
            {
                errors++;
                LogRowFailed(logger, ex, row.Repo, row.PrNumber);
            }
        }

        var picked = await budget.PickRedrivesAsync([.. candidates.Select(c => new RedriveCandidate(c.Row.Repo, c.Row.PrNumber))], cancellationToken).ConfigureAwait(false);
        var redriven = 0;
        foreach (var pick in picked)
        {
            var (row, pr) = candidates.First(c => c.Row.Repo == pick.Repo && c.Row.PrNumber == pick.PrNumber);
            await SignalAsync(pick.Repo, pick.PrNumber, pr.HeadSha, cancellationToken).ConfigureAwait(false);
            await pendings.MarkRedrivenAsync(row.Repo, row.PrNumber, row.HeadSha, cancellationToken).ConfigureAwait(false);
            redriven++;
        }

        return new RedriveResult(open.Count, running, settled, redriven, errors, skipped);
    }

    [Activity(RedeliverName)]
    public async Task<RedeliverResult> RedeliverAsync()
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var now = time.GetUtcNow();
        var windowStart = now - Lookback;
        if ((await state.GetAsync(cancellationToken).ConfigureAwait(false)).LastSeenDeliveredAt is { } lastSeen && lastSeen - DeliveryOverlap > windowStart)
        {
            windowStart = lastSeen - DeliveryOverlap;
        }

        var deliveries = new List<HookDelivery>();
        string? cursor = null;
        var pages = 0;
        while (pages < MaxDeliveryPages)
        {
            var page = await app.ListHookDeliveriesAsync(cursor, cancellationToken).ConfigureAwait(false);
            pages++;
            deliveries.AddRange(page.Deliveries);
            if (page.Deliveries.Count == 0 || page.NextCursor is null || page.Deliveries.Min(d => d.DeliveredAt) < windowStart)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        var plan = HookRedeliveryPlanner.Plan(deliveries, windowStart);
        var redelivered = 0;
        foreach (var deliveryId in plan)
        {
            if (await app.RedeliverAsync(deliveryId, cancellationToken).ConfigureAwait(false))
            {
                redelivered++;
            }
        }

        if (deliveries.Count > 0)
        {
            await state.SaveLastSeenAsync(deliveries.Max(d => d.DeliveredAt), cancellationToken).ConfigureAwait(false);
        }

        return new RedeliverResult(pages, plan.Count, redelivered, plan.Count - redelivered);
    }

    [Activity(ReconcileName)]
    public async Task<ReconcileResult> ReconcileAsync()
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var since = time.GetUtcNow() - Lookback;
        var repos = await app.ListInstallationRepositoriesAsync(TriageRules.AllowedOwner, cancellationToken).ConfigureAwait(false);
        int signalled = 0, failed = 0, errors = 0;
        foreach (var repo in repos.Where(TriageRules.IsAllowedOwner))
        {
            try
            {
                var prs = await github.ListOpenPullRequestsAsync(repo, since, cancellationToken).ConfigureAwait(false);
                foreach (var pr in prs.Where(p => p.AuthorId == TriageRules.RenovateUserId && TriageRules.IsSameRepo(repo, p.HeadRepo, p.BaseRepo)))
                {
                    switch (await ReconcileAsync(repo, pr, cancellationToken).ConfigureAwait(false))
                    {
                        case Reconciled.Signalled:
                            signalled++;
                            break;
                        case Reconciled.Failed:
                            failed++;
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (GitHubApiException ex)
            {
                errors++;
                LogRepoFailed(logger, ex, repo);
            }
        }

        return new ReconcileResult(repos.Count, signalled, failed, errors);
    }

    [Activity(RecordSuccessName)]
    public Task RecordSuccessAsync() => state.SaveSuccessAsync(ActivityExecutionContext.Current.CancellationToken);

    private enum RowState
    {
        Redrive,
        Settled,
        Skipped,
    }

    private enum Reconciled
    {
        Nothing,
        Signalled,
        Failed,
    }

    private async Task<(RowState State, GitHubPullRequest Pr)> AssessRowAsync(TriagePending row, CancellationToken cancellationToken)
    {
        var pr = await github.GetPullRequestAsync(row.Repo, row.PrNumber, cancellationToken).ConfigureAwait(false);
        TriagePendingStatus? status = null;
        var endsSkipped = false;
        if (TriageRules.IsNotOpen(pr.State, pr.Merged, shadow: false))
        {
            status = TriagePendingStatus.OwnerHandled;
        }
        else if (pr.Labels.Contains(TriageRules.NeedsReviewLabel, StringComparer.Ordinal))
        {
            status = TriagePendingStatus.Owner;
        }
        else
        {
            var reviews = await github.ListReviewsAsync(row.Repo, row.PrNumber, cancellationToken).ConfigureAwait(false);
            var events = await github.ListIssueEventsAsync(row.Repo, row.PrNumber, cancellationToken).ConfigureAwait(false);
            if (OwnerHandling.IsHandled(row.OpenedAt, reviews, events))
            {
                status = TriagePendingStatus.OwnerHandled;
            }
            else
            {
                var unapplied = await verdicts.FindUnappliedAsync(row.Repo, row.PrNumber, pr.HeadSha, cancellationToken).ConfigureAwait(false);
                var ignoreTriageSignals = unapplied is not null;
                var honourNeedsReviewLabel = unapplied is not null && TriageRules.IsApproving(unapplied.Verdict);
                endsSkipped = TriageRules.SkipReason(pr.AuthorId, pr.Labels, ignoreTriageSignals, honourNeedsReviewLabel, hasPendingRow: true) is not null
                    || TriageRules.IsApproved(reviews, pr.HeadSha, ignoreTriageSignals);
            }
        }

        if (status is not { } settled)
        {
            await pendings.SetHeldAsync(row.Repo, row.PrNumber, row.HeadSha, pr.Labels.Contains(TriageRules.BlockedLabel, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            return (endsSkipped ? RowState.Skipped : RowState.Redrive, pr);
        }

        await pendings.SetStatusAsync(row.Repo, row.PrNumber, row.HeadSha, settled, cancellationToken).ConfigureAwait(false);
        await CompleteOpenCheckAsync(row.Repo, row.PrNumber, row.HeadSha, "neutral", "No further agent triage: this pull request is no longer waiting in the queue.", cancellationToken).ConfigureAwait(false);
        return (RowState.Settled, pr);
    }

    private async Task<Reconciled> ReconcileAsync(string repo, GitHubListedPullRequest pr, CancellationToken cancellationToken)
    {
        var check = await github.FindCheckRunAsync(repo, pr.HeadSha, TriageCheckActivities.CheckName, TriageCheckActivities.ExternalId(repo, pr.Number, pr.HeadSha), cancellationToken).ConfigureAwait(false);
        if (check is { Status: "completed" })
        {
            return Reconciled.Nothing;
        }

        var row = await pendings.FindAsync(repo, pr.Number, cancellationToken).ConfigureAwait(false);
        if (row is { Status: TriagePendingStatus.Pending or TriagePendingStatus.Redriven }
            || await probe.IsRunningAsync(repo, pr.Number, cancellationToken).ConfigureAwait(false))
        {
            return Reconciled.Nothing;
        }

        if (check is not null)
        {
            await CompleteOpenCheckAsync(repo, pr.Number, pr.HeadSha, "failure", "The triage run ended without finishing this check.", cancellationToken).ConfigureAwait(false);
            return Reconciled.Failed;
        }

        if (pr.Labels.Any(l => TriageRules.IsBlocking(l, hasPendingRow: false))
            || await verdicts.ExistsAsync(repo, pr.Number, pr.HeadSha, options.Value.Mode, cancellationToken).ConfigureAwait(false))
        {
            return Reconciled.Nothing;
        }

        await SignalAsync(repo, pr.Number, pr.HeadSha, cancellationToken).ConfigureAwait(false);
        return Reconciled.Signalled;
    }

    private async Task CompleteOpenCheckAsync(string repo, int prNumber, string headSha, string conclusion, string summary, CancellationToken cancellationToken)
    {
        var externalId = TriageCheckActivities.ExternalId(repo, prNumber, headSha);
        var check = await github.FindCheckRunAsync(repo, headSha, TriageCheckActivities.CheckName, externalId, cancellationToken).ConfigureAwait(false);
        if (check is null || check.Status == "completed")
        {
            return;
        }

        await github.UpdateCheckRunAsync(
            repo,
            check.Id,
            new GitHubCheckRunWrite(TriageCheckActivities.CheckName, headSha, externalId, "completed", conclusion, TriageCheckActivities.CheckName, summary),
            cancellationToken).ConfigureAwait(false);
    }

    private Task SignalAsync(string repo, int prNumber, string headSha, CancellationToken cancellationToken) =>
        signaler.SignalAsync(new CheckSuiteCompletedSignal(repo, prNumber, headSha, 0, SweeperSource, null, SweeperSource), cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sweeper could not re-drive {Repo}#{PrNumber}; it is tried again next run.")]
    private static partial void LogRowFailed(ILogger logger, Exception exception, string repo, int prNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sweeper could not reconcile {Repo}; it is tried again next run.")]
    private static partial void LogRepoFailed(ILogger logger, Exception exception, string repo);
}
