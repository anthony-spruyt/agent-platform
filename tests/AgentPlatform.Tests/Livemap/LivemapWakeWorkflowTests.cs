using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Livemap;

public class LivemapWakeWorkflowTests
{
    private const long SuiteId = 77;

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly LivemapBuildWake Wake = new("1111111111111111111111111111111111111111", SuiteId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LivemapBuildStore Store, LivemapWakeActivitiesTests.FakeSignaler Signaler, LivemapWakeActivities Activities)> RigAsync(bool buildInFlight)
    {
        var store = new LivemapBuildStore(InMemoryRunData.Create().Factory, new FixedTimeProvider(Now));
        if (buildInFlight)
        {
            await store.RecordAsync(400, "444", Now, LivemapBuildState.Detected, Ct);
        }

        var signaler = new LivemapWakeActivitiesTests.FakeSignaler();
        return (store, signaler, new LivemapWakeActivities(store, signaler));
    }

    private static async Task<T> InWorkerAsync<T>(WorkflowEnvironment env, LivemapWakeActivities activities, Func<Task<T>> body)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapWakeWorkflow>()
                .AddAllActivities(activities));
        return await worker.ExecuteAsync(body, Ct);
    }

    private static Task<WorkflowHandle<LivemapWakeWorkflow, LivemapWakeOutcome>> StartAsync(WorkflowEnvironment env) =>
        env.Client.StartWorkflowAsync(
            (LivemapWakeWorkflow wf) => wf.RunAsync(Wake),
            new WorkflowOptions($"{LivemapWakeWorkflow.WorkflowId(SuiteId)}-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

    [Fact]
    public void TheWorkflowId_NamesTheCheckSuite()
    {
        Assert.Equal("livemap-wake-77", LivemapWakeWorkflow.WorkflowId(SuiteId));
    }

    [Fact]
    public async Task TheBuildInFlight_IsWoken()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (_, signaler, activities) = await RigAsync(buildInFlight: true);

        var outcome = await InWorkerAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            var result = await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct });
            await HistoryStore.RecordAsync(handle, "livemap-wake-woken");
            return result;
        });

        Assert.Equal(LivemapWakeOutcome.Woken, outcome);
        Assert.Equal((400L, Wake), Assert.Single(signaler.Signalled));
    }

    [Fact]
    public async Task WithNoBuildInFlight_TheWorkflowEndsAtOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (_, signaler, activities) = await RigAsync(buildInFlight: false);

        var outcome = await InWorkerAsync(env, activities, async () =>
            await (await StartAsync(env)).GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }));

        Assert.Equal(LivemapWakeOutcome.NoBuildInFlight, outcome);
        Assert.Empty(signaler.Signalled);
    }

    [Fact]
    public async Task ASignalThatFailsOnce_IsTriedAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (_, signaler, activities) = await RigAsync(buildInFlight: true);
        signaler.Failure = new InvalidOperationException("Temporal is down");
        signaler.FailFirstCalls = 1;

        var outcome = await InWorkerAsync(env, activities, async () =>
            await (await StartAsync(env)).GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }));

        Assert.Equal(LivemapWakeOutcome.Woken, outcome);
        Assert.Single(signaler.Signalled);
    }

    [Fact]
    public async Task ASignalThatNeverWorks_FailsTheWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (_, signaler, activities) = await RigAsync(buildInFlight: true);
        signaler.Failure = new InvalidOperationException("Temporal is down");

        await InWorkerAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }));
            return 0;
        });
    }
}
