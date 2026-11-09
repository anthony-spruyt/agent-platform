using AgentPlatform.Host.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace AgentPlatform.Host.Livemap;

public interface ILivemapBuildStarter
{
    Task StartAsync(long buildId, string manifestGid, CancellationToken cancellationToken);
}

public sealed class TemporalLivemapBuildStarter(ITemporalClient client) : ILivemapBuildStarter
{
    // A running workflow for the build is kept, so every poll can start one without doubling the work; a closed one is started again.
    public async Task StartAsync(long buildId, string manifestGid, CancellationToken cancellationToken) =>
        await client.StartWorkflowAsync(
            (LivemapBuildWorkflow workflow) => workflow.RunAsync(new LivemapBuildInput(buildId, manifestGid)),
            new WorkflowOptions(LivemapBuildWorkflow.WorkflowId(buildId), TaskQueues.AgentPlatform)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);
}
