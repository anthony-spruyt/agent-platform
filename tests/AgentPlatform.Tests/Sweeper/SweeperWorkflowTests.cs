using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Sweeper;

public class SweeperWorkflowTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = FakeGitHubApi.Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, SweeperActivitiesTests.Rig rig, Func<Task<T>> body)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<RenovateTriageSweeperWorkflow>()
                .AddAllActivities(rig.Activities)
                .AddAllActivities(rig.Data.Activities));
        return await worker.ExecuteAsync(body, Ct);
    }

    private static async Task<WorkflowHandle<RenovateTriageSweeperWorkflow>> SweepAsync(WorkflowEnvironment env, string? record = null)
    {
        var handle = await env.Client.StartWorkflowAsync(
            (RenovateTriageSweeperWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions($"{RenovateTriageSweeperWorkflow.WorkflowId}-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
        await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct });
        if (record is not null)
        {
            await HistoryStore.RecordAsync(handle, record);
        }

        return handle;
    }

    private static async Task<List<Run>> RunsAsync(SweeperActivitiesTests.Rig rig)
    {
        await using var db = await rig.Data.Factory.CreateDbContextAsync(Ct);
        return await db.Runs.Include(r => r.Steps).OrderBy(r => r.StartedAt).ToListAsync(Ct);
    }

    [Fact]
    public async Task ASweep_RunsTheThreeStepsInOrder_RecordsThemInTheRunData_AndStampsTheSuccessTime()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig();
        rig.App.Repositories.Add(Repo);

        await InWorkerAsync(env, rig, async () => await SweepAsync(env, "renovate-triage-sweeper-happy-path"));

        var run = Assert.Single(await RunsAsync(rig));
        Assert.Equal(nameof(RenovateTriageSweeperWorkflow), run.WorkflowType);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["redrive", "redeliver", "reconcile"], run.Steps.OrderBy(s => s.Seq).Select(s => s.Name));
        Assert.All(run.Steps, s => Assert.Equal(("completed", LadderRung.Rule), (s.StopReason, s.Rung)));
        Assert.NotNull((await rig.Sweeper.GetAsync(Ct)).LastSuccessAt);
    }

    [Fact]
    public async Task AParkedPullRequest_IsRedrivenOnceTheCooldownHasEnded_AndTheStepShowsInTheRunData()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        await rig.CooldownAsync();

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));
        Assert.Empty(rig.Signaler.Signals);

        await using (var db = await rig.Data.Factory.CreateDbContextAsync(Ct))
        {
            db.ClaudeCooldowns.RemoveRange(db.ClaudeCooldowns);
            await db.SaveChangesAsync(Ct);
        }

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));

        Assert.Equal(7, Assert.Single(rig.Signaler.Signals).PrNumber);
        var redrive = (await RunsAsync(rig)).Last().Steps.Single(s => s.Name == "redrive");
        Assert.Contains("\"redriven\":1", redrive.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASweep_RedrivesOnlyWithinTheCap_AndSkipsOwnerHandledAndNeedsHumanReviewPullRequests()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig(perHour: 2);
        foreach (var pr in new[] { 1, 2, 3, 4, 5 })
        {
            await rig.ParkAsync(pr);
        }

        rig.OpenPullRequest(1, TriageRules.NeedsReviewLabel);
        rig.Github.PullRequestsByNumber[2] = FakeGitHubApi.Renovate() with { Number = 2, State = "closed" };
        rig.OpenPullRequest(3);
        rig.OpenPullRequest(4);
        rig.OpenPullRequest(5);

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));

        Assert.Equal([3, 4], rig.Signaler.Signals.Select(s => s.PrNumber));
        Assert.Equal(TriagePendingStatus.Owner, await rig.StatusAsync(1));
        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(2));
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(5));
    }

    [Fact]
    public async Task AFailedDelivery_IsRedeliveredByTheSweep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig();
        rig.App.Pages.Add([new HookDelivery(9, "g-9", rig.Now.AddMinutes(-5), false, 502, "check_suite")]);

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));

        Assert.Equal([9L], rig.App.Redelivered);
        var step = Assert.Single((await RunsAsync(rig)).Single().Steps, s => s.Name == "redeliver");
        Assert.Contains("\"redelivered\":1", step.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenTriageCheckNothingIsWorkingOn_IsCompletedAsFailureByTheSweep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig();
        rig.App.Repositories.Add(Repo);
        rig.Github.OpenPullRequests.Add(new GitHubListedPullRequest(7, TriageRules.RenovateUserId, "renovate/dep", Sha, Repo, Repo, rig.Now.AddHours(-1), []));
        rig.OpenCheck(7);

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));

        Assert.Equal("failure", Assert.Single(rig.Github.StoredCheckRuns).Run.Conclusion);
    }

    [Fact]
    public async Task AFailingStep_IsRecordedAsAnError_DoesNotStopTheOthers_AndLeavesTheSuccessTimeAlone()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new SweeperActivitiesTests.Rig();
        rig.App.Failure = new GitHubApiException("GitHub is down", System.Net.HttpStatusCode.BadGateway);
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);

        await InWorkerAsync(env, rig, async () => await SweepAsync(env));

        var run = Assert.Single(await RunsAsync(rig));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(["completed", "error", "completed"], run.Steps.OrderBy(s => s.Seq).Select(s => s.StopReason));
        Assert.Single(rig.Signaler.Signals);
        Assert.Null((await rig.Sweeper.GetAsync(Ct)).LastSuccessAt);
    }
}
