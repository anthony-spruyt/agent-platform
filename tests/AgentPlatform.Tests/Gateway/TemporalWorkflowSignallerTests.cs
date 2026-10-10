using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.Workflows;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Gateway;

public class TemporalWorkflowSignallerTests
{
    private static readonly SubmitResultPayload Payload =
        new("success", "from the agent", JsonDocument.Parse("""{"n":1}""").RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class HoldingActivities(TaskCompletionSource release)
    {
        [Activity("RunAgentPod")]
        public async Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
        {
            await release.Task;
            return new AgentRunResult(AgentRunOutcome.Succeeded, "pod", 0, "ok", null, null);
        }
    }

    [Fact]
    public async Task Signal_ReachesTheNamedRunOfTheWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var release = new TaskCompletionSource();
        using var workflows = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<SpikeWorkflow>());
        using var pods = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(new HoldingActivities(release)));

        var result = await workflows.ExecuteAsync(() => pods.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (SpikeWorkflow wf) => wf.RunAsync(new SpikeInput("p", AgentTier.Read)),
                    new WorkflowOptions($"spike-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                var signaller = new TemporalWorkflowSignaller(env.Client);

                await signaller.SignalResultAsync(new AgentRunIdentity(handle.Id, handle.ResultRunId!), Payload, Ct);
                release.SetResult();
                return await handle.GetResultAsync();
            },
            Ct), Ct);

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal("from the agent", result.Result!.Summary);
    }

    [Fact]
    public async Task Signal_ToAnUnknownWorkflow_ThrowsRunNotActive()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var signaller = new TemporalWorkflowSignaller(env.Client);

        await Assert.ThrowsAsync<RunNotActiveException>(
            () => signaller.SignalResultAsync(new AgentRunIdentity("does-not-exist", "run"), Payload, Ct));
    }
}
