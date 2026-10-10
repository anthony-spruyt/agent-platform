using AgentPlatform.Host.Workflows;
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
            () => env.Client.ExecuteWorkflowAsync(
                (PingWorkflow wf) => wf.RunAsync("platform"),
                new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform)),
            ct);

        Assert.Equal("pong: platform", result);
    }
}
