using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Livemap;

public class TemporalLivemapWakeStarterTests
{
    private static readonly LivemapBuildWake Wake = new("1111111111111111111111111111111111111111", 77);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string?> RunIdAsync(WorkflowEnvironment env, long suiteId)
    {
        var description = await env.Client.GetWorkflowHandle(LivemapWakeWorkflow.WorkflowId(suiteId)).DescribeAsync();
        return description.RunId;
    }

    [Fact]
    public async Task StartingTheSameSuiteWhileItsWakeRuns_KeepsTheRunThatIsThere()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var starter = new TemporalLivemapWakeStarter(env.Client);

        await starter.StartAsync(Wake, Ct);
        var first = await RunIdAsync(env, 77);
        await starter.StartAsync(Wake, Ct);

        Assert.Equal(first, await RunIdAsync(env, 77));
    }

    [Fact]
    public async Task EachSuite_GetsItsOwnWake()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var starter = new TemporalLivemapWakeStarter(env.Client);

        await starter.StartAsync(Wake, Ct);
        await starter.StartAsync(Wake with { SuiteId = 78 }, Ct);

        Assert.NotEqual(await RunIdAsync(env, 77), await RunIdAsync(env, 78));
    }

    [Fact]
    public async Task ASuiteWhoseWakeHasEnded_IsWokenAgain_BecauseARerunFinishesUnderTheSameId()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var starter = new TemporalLivemapWakeStarter(env.Client);
        await starter.StartAsync(Wake, Ct);
        var first = await RunIdAsync(env, 77);
        await env.Client.GetWorkflowHandle(LivemapWakeWorkflow.WorkflowId(77)).TerminateAsync();

        await starter.StartAsync(Wake, Ct);

        Assert.NotEqual(first, await RunIdAsync(env, 77));
    }

    [Fact]
    public async Task TheWorkerPicksUpTheWake_WithItsCommitAndSuite()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var store = new LivemapBuildStore(InMemoryRunData.Create().Factory, new FixedTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        await store.RecordAsync(400, "444", new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), LivemapBuildState.Detected, Ct);
        var signaler = new LivemapWakeActivitiesTests.FakeSignaler();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapWakeWorkflow>()
                .AddAllActivities(new LivemapWakeActivities(store, signaler)));

        await worker.ExecuteAsync(
            async () =>
            {
                await new TemporalLivemapWakeStarter(env.Client).StartAsync(Wake, Ct);
                using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                giveUp.CancelAfter(TimeSpan.FromSeconds(20));
                while (signaler.Signalled.Count == 0)
                {
                    await Task.Delay(20, giveUp.Token);
                }
            },
            Ct);

        Assert.Equal((400L, Wake), Assert.Single(signaler.Signalled));
    }
}
