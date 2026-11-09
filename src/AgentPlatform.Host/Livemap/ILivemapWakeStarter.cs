using AgentPlatform.Host.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace AgentPlatform.Host.Livemap;

public interface ILivemapWakeStarter
{
    Task StartAsync(LivemapBuildWake wake, CancellationToken cancellationToken);
}

public sealed class TemporalLivemapWakeStarter(ITemporalClient client) : ILivemapWakeStarter
{
    // A wake still running for the suite is kept; a closed one starts again, since a re-run of a suite finishes under the same id.
    public async Task StartAsync(LivemapBuildWake wake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wake);

        await client.StartWorkflowAsync(
            (LivemapWakeWorkflow workflow) => workflow.RunAsync(wake),
            new WorkflowOptions(LivemapWakeWorkflow.WorkflowId(wake.SuiteId), TaskQueues.AgentPlatform)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);
    }
}
