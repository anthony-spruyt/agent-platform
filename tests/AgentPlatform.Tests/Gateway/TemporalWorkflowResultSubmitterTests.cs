using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Gateway;

public class TemporalWorkflowResultSubmitterTests
{
    private static readonly SubmitResultPayload Payload =
        new("success", "from the agent", JsonDocument.Parse("""{"n":1}""").RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class HoldingActivities(TaskCompletionSource release)
    {
        private int _deletes;

        [Activity("RunAgentPod")]
        public async Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
        {
            await release.Task;
            return new AgentRunResult(AgentRunOutcome.Succeeded, "pod", 0, "ok", null, null);
        }

        [Activity("DeleteAgentPod")]
        public Task DeleteAgentPodAsync(string step)
        {
            Interlocked.Increment(ref _deletes);
            return Task.CompletedTask;
        }
    }

    private static async Task<T> WithRunningWorkflowAsync<T>(
        TaskCompletionSource release,
        Func<WorkflowEnvironment, WorkflowHandle<SpikeWorkflow, SpikeResult>, Task<T>> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new HoldingActivities(release);
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<SpikeWorkflow>().AddAllActivities(activities).AddAllActivities(InMemoryRunData.Create().Activities));
        using var pods = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(activities));

        return await workflows.ExecuteAsync(() => pods.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (SpikeWorkflow wf) => wf.RunAsync(new SpikeInput("p", AgentTier.Read)),
                    new WorkflowOptions($"spike-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                return await body(env, handle);
            },
            Ct), Ct);
    }

    [Fact]
    public async Task Submit_ReachesTheNamedRunOfTheWorkflow()
    {
        var release = new TaskCompletionSource();

        var result = await WithRunningWorkflowAsync(release, async (env, handle) =>
        {
            var submitter = new TemporalWorkflowResultSubmitter(env.Client);

            await submitter.SubmitResultAsync(new AgentRunIdentity(handle.Id, handle.ResultRunId!), Payload, Ct);
            release.SetResult();
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal("from the agent", result.Result!.Summary);
    }

    [Fact]
    public async Task Submit_TwiceForTheSameRun_RejectsTheSecondAndKeepsTheFirst()
    {
        var release = new TaskCompletionSource();

        var result = await WithRunningWorkflowAsync(release, async (env, handle) =>
        {
            var submitter = new TemporalWorkflowResultSubmitter(env.Client);
            var run = new AgentRunIdentity(handle.Id, handle.ResultRunId!);

            await submitter.SubmitResultAsync(run, Payload, Ct);
            await Assert.ThrowsAsync<ResultAlreadySubmittedException>(
                () => submitter.SubmitResultAsync(run, Payload with { Summary = "second" }, Ct));
            release.SetResult();
            return await handle.GetResultAsync();
        });

        Assert.Equal("from the agent", result.Result!.Summary);
    }

    [Fact]
    public async Task Submit_AfterTheWorkflowEnded_ThrowsRunNotActive()
    {
        var release = new TaskCompletionSource();
        release.SetResult();

        await WithRunningWorkflowAsync(release, async (env, handle) =>
        {
            await handle.GetResultAsync();
            var submitter = new TemporalWorkflowResultSubmitter(env.Client);

            await Assert.ThrowsAsync<RunNotActiveException>(
                () => submitter.SubmitResultAsync(new AgentRunIdentity(handle.Id, handle.ResultRunId!), Payload, Ct));
            return 0;
        });
    }

    [Fact]
    public async Task Submit_ToAnUnknownWorkflow_ThrowsRunNotActive()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var submitter = new TemporalWorkflowResultSubmitter(env.Client);

        await Assert.ThrowsAsync<RunNotActiveException>(
            () => submitter.SubmitResultAsync(new AgentRunIdentity("does-not-exist", "run"), Payload, Ct));
    }
}
