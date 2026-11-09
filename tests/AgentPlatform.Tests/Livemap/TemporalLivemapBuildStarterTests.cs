using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class TemporalLivemapBuildStarterTests
{
    private static async Task<string?> RunIdAsync(WorkflowEnvironment env)
    {
        var description = await env.Client.GetWorkflowHandle(LivemapBuildWorkflow.WorkflowId(BuildId)).DescribeAsync();
        return description.RunId;
    }

    [Fact]
    public async Task StartingABuildThatIsAlreadyRunning_KeepsTheRunThatIsThere()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var starter = new TemporalLivemapBuildStarter(env.Client);

        await starter.StartAsync(BuildId, Gid, Ct);
        var first = await RunIdAsync(env);
        await starter.StartAsync(BuildId, Gid, Ct);

        Assert.Equal(first, await RunIdAsync(env));
    }

    [Fact]
    public async Task StartingABuildWhoseRunHasEnded_StartsItAgain()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var starter = new TemporalLivemapBuildStarter(env.Client);
        await starter.StartAsync(BuildId, Gid, Ct);
        var first = await RunIdAsync(env);
        await env.Client.GetWorkflowHandle(LivemapBuildWorkflow.WorkflowId(BuildId)).TerminateAsync();

        await starter.StartAsync(BuildId, Gid, Ct);

        Assert.NotEqual(first, await RunIdAsync(env));
    }

    [Fact]
    public async Task TheWorkerPicksUpTheStartedBuild_WithItsIdAndManifest()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();
        rig.GitHub.ScriptRun(FakeLivemapGitHub.ExeCaptureRunId, Running(FakeLivemapGitHub.ExeCaptureRunId));
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<LivemapBuildWorkflow>()
                .AddAllActivities(rig.Activities));

        await worker.ExecuteAsync(
            async () =>
            {
                await new TemporalLivemapBuildStarter(env.Client).StartAsync(BuildId, Gid, Ct);
                using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                giveUp.CancelAfter(TimeSpan.FromSeconds(20));
                while (rig.GitHub.Dispatches.Count == 0)
                {
                    await Task.Delay(20, giveUp.Token);
                }
            },
            Ct);

        Assert.Equal((LivemapWorkflowFile.ExeCapture, BuildId, Gid), rig.GitHub.Dispatches[0]);
    }
}
