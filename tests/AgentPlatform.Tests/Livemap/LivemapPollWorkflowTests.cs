using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPollWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, LivemapPollActivitiesTests.Rig rig, Func<Task<T>> body)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapPollWorkflow>()
                .AddAllActivities(rig.Activities));
        return await worker.ExecuteAsync(body, Ct);
    }

    private static Task<WorkflowHandle<LivemapPollWorkflow, LivemapPollResult>> StartAsync(WorkflowEnvironment env) =>
        env.Client.StartWorkflowAsync(
            (LivemapPollWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions($"{LivemapPollWorkflow.WorkflowId}-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

    [Fact]
    public async Task APoll_RecordsTheNewBuild_AndReturnsWhatItFound()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapPollActivitiesTests.Rig();

        var result = await InWorkerAsync(env, rig, async () =>
        {
            var handle = await StartAsync(env);
            var outcome = await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct });
            await HistoryStore.RecordAsync(handle, "livemap-poll-detected");
            return outcome;
        });

        Assert.Equal(new LivemapPollResult(LivemapPollOutcome.Detected, 26430253), result);
        Assert.NotNull(await rig.Store.FindAsync(26430253, Ct));
    }

    [Fact]
    public async Task ATransientSteamFailure_IsRetried()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapPollActivitiesTests.Rig();
        rig.Steam.FailFirst(1, new SteamAppInfoException("blip"));

        var result = await InWorkerAsync(env, rig, async () => await (await StartAsync(env)).GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }));

        Assert.Equal(LivemapPollOutcome.Detected, result.Outcome);
        Assert.Equal(2, rig.Steam.Calls);
    }

    [Fact]
    public async Task APollThatKeepsFailing_FailsTheRun_SoTheNextScheduledPollStartsFresh()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapPollActivitiesTests.Rig();
        rig.Steam.Failure = new SteamAppInfoException("down");

        var failure = await InWorkerAsync(env, rig, async () =>
            await Assert.ThrowsAsync<WorkflowFailedException>(async () => await (await StartAsync(env)).GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct })));

        Assert.IsType<ActivityFailureException>(failure.InnerException);
        Assert.Equal(3, rig.Steam.Calls);
        Assert.Equal(0, await rig.RowCountAsync());
    }
}
