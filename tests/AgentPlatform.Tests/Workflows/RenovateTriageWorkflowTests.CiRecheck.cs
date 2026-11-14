using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Workflows;

public partial class RenovateTriageWorkflowTests
{
    private static readonly GitHubCheckRun _failedTest = new(2, "test", "completed", "failure", "github-actions");

    private static async Task<Rig> ApprovedByTheAppAsync()
    {
        var rig = new Rig { Mode = TriageMode.Live };
        rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", ShaA, null));
        rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.AutoApprovedLabel] };
        rig.Github.Checks.Add(_failedTest);
        await rig.Data.Verdicts.RecordAsync(Repo, 7, ShaA, new TriageDecision(TriageVerdict.Approve, TriageRules.Digest, "**Auto-approved:** digest bump."), TriageMode.Live, null, Ct);
        await rig.Data.Verdicts.MarkAppliedAsync(Repo, 7, ShaA, TriageVerdict.Approve, VerdictAction.Approve, Ct);
        return rig;
    }

    private static async Task<List<string?>> TriggersAsync(Rig rig)
    {
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var runs = await db.Runs.OrderBy(r => r.StartedAt).ToListAsync(Ct);
        return [.. runs.Select(r => JsonDocument.Parse(r.InputEvent).RootElement.GetProperty("trigger").GetString())];
    }

    [Fact]
    public async Task LiveMode_AnApprovedHeadWhoseCiFailed_IsFlaggedAndItsApprovalWithdrawn_WithoutJevOrTheAgent()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal()), "renovate-triage-ci-recheck-withdrawn"));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ApprovalWithdrawn, TriageVerdict.NeedsReview), result);
        Assert.Equal([1L], rig.Github.Dismissed);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        Assert.Contains("`test`", Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        var verdict = Assert.Single(await VerdictsAsync(rig));
        Assert.Equal(TriageVerdict.NeedsReview, verdict.Verdict);
        Assert.NotNull(verdict.AppliedAt);
        Assert.Empty(rig.Jev.Bodies);
        Assert.Equal(["assess", "withdraw"], (await StepsAsync(rig)).Select(s => s.Name));
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.Runs.CountAsync(Ct));
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Success), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AnApprovedHeadWhoseCiIsStillGreen_IsLeftAlone()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();
        rig.Github.Checks.Remove(_failedTest);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(RenovateTriageOutcome.Skipped, result.Outcome);
        Assert.Empty(rig.Github.Writes);
        Assert.Equal(["assess"], (await StepsAsync(rig)).Select(s => s.Name));
    }

    [Fact]
    public async Task LiveMode_AnApprovalGitHubWillNotDismiss_LeavesThePrFlagged_AndClosesTheCheckAsFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();
        rig.Github.OnDismiss = () => throw new GitHubApiException("unprocessable", System.Net.HttpStatusCode.UnprocessableEntity);

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(TriageVerdict.NeedsReview, result.Verdict);
        Assert.Equal([TriageRules.NeedsReviewLabel], rig.Github.AddedLabels);
        Assert.Single(rig.Github.Comments);
        Assert.Equal("DismissRefused", (await StepsAsync(rig)).Single(s => s.Name == "withdraw").StopReason);
        var last = rig.CheckRuns.Reports[^1];
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Failure), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task LiveMode_AWithdrawalThatFailsAfterTheLabelIsSet_FinishesOnRetryWhenCiPassesAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();
        var attempts = 0;
        rig.Github.OnDismiss = () =>
        {
            if (attempts++ == 0)
            {
                rig.Github.Checks.Remove(_failedTest);
                throw new GitHubApiException("bad gateway", System.Net.HttpStatusCode.BadGateway);
            }
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal(new RenovateTriageResult(RenovateTriageOutcome.Judged, ShaA, TriageRules.ApprovalWithdrawn, TriageVerdict.NeedsReview), result);
        Assert.Equal([1L], rig.Github.Dismissed);
        Assert.Contains("`test`", Assert.Single(rig.Github.Comments).Body, StringComparison.Ordinal);
        Assert.Equal(TriageVerdict.NeedsReview, Assert.Single(await VerdictsAsync(rig)).Verdict);
    }

    [Fact]
    public async Task LiveMode_AHeadThatMovedBeforeTheWithdraw_IsLeftAsItIs_AndTheCheckClosesAsNeutral()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();
        rig.Github.OnPullRequestRead = () =>
        {
            if (rig.Github.Calls.Count(c => c == "pr") == 2)
            {
                rig.Github.PullRequest = rig.Github.PullRequest with { HeadSha = ShaB };
            }
        };

        var result = await InWorkerAsync(env, rig, async () => await RunToResultAsync(await StartAsync(env, Signal())));

        Assert.Equal((RenovateTriageOutcome.Skipped, RenovateTriageActivities.HeadMovedReason), (result.Outcome, result.Reason));
        Assert.Empty(rig.Github.Writes);
        var last = rig.CheckRuns.Reports.Last(r => r.HeadSha == ShaA);
        Assert.Equal((CheckRunStatus.Completed, CheckRunConclusion.Neutral), (last.Status, last.Conclusion));
    }

    [Fact]
    public async Task ASignalThatArrivesAfterTheLastAssess_StartsAFreshRunInsteadOfEndingTheOldOne()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = await ApprovedByTheAppAsync();

        await InWorkerAsync(env, rig, async () =>
        {
            var reads = 0;
            rig.Github.OnCheckRunsRead = async () =>
            {
                if (++reads == 2)
                {
                    await SignalAsync(env, Signal(suite: 2));
                }
            };
            var handle = await StartAsync(env, Signal());
            var first = Handle(env, handle.Id, handle.ResultRunId);
            var latest = await RunToResultAsync(handle);
            await HistoryStore.RecordAsync(first, "renovate-triage-ci-recheck-continued");
            return latest;
        });

        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        var runs = await db.Runs.Include(r => r.Steps).OrderBy(r => r.StartedAt).ToListAsync(Ct);
        Assert.Equal(2, runs.Count);
        Assert.Equal(["assess", "withdraw"], runs[0].Steps.OrderBy(s => s.Seq).Select(s => s.Name));
        var fresh = Assert.Single(runs[1].Steps);
        Assert.Equal(("assess", "Skipped"), (fresh.Name, fresh.StopReason));
        Assert.Equal([1L], rig.Github.Dismissed);
        Assert.Equal(["signal", "signal"], await TriggersAsync(rig));
    }

    [Fact]
    public async Task ARunContinuedByALateSignal_RecordsTheSignal_AndTheNewHeadRunAfterItRecordsNewHead()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new Rig();
        var renovate = rig.Github.PullRequest;
        rig.Github.PullRequest = renovate with { AuthorId = 5 };
        rig.Github.Checks.Add(new GitHubCheckRun(2, "test", "in_progress", null, "github-actions"));
        var reads = 0;
        rig.Github.OnPullRequestRead = () =>
        {
            if (++reads == 1)
            {
                SignalAsync(env, Signal(suite: 2)).GetAwaiter().GetResult();
            }
            else if (reads == 2)
            {
                rig.Github.PullRequest = renovate;
            }
        };

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env, Signal());
            await WaitForStepAsync(rig, "assess", "Pending");
            rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: ShaB);
            rig.Github.Checks.Clear();
            rig.Github.Checks.Add(new GitHubCheckRun(3, "test", "completed", "success", "github-actions"));
            await SignalAsync(env, Signal(ShaB, suite: 3));
            return await RunToResultAsync(handle);
        });

        Assert.Equal(ShaB, result.HeadSha);
        Assert.Equal(["signal", "signal", "new-head"], await TriggersAsync(rig));
    }
}
