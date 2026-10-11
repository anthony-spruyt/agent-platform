using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests;

public class PingWorkflowTests
{
    [Fact]
    public async Task RunAsync_ReturnsPongFromActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<PingWorkflow>()
                .AddAllActivities(new PingActivities()));

        var result = await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                var output = await handle.GetResultAsync();
                await HistoryStore.RecordAsync(handle, "ping-happy-path");
                return output;
            },
            ct);

        Assert.Equal("pong: platform", result);
    }
}
