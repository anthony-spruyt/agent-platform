using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Livemap;

public interface ILivemapBuildSignaler
{
    // False when the build has no running workflow to wake
    Task<bool> SignalAsync(long buildId, LivemapBuildWake wake, CancellationToken cancellationToken);
}

public sealed class TemporalLivemapBuildSignaler(ITemporalClient client) : ILivemapBuildSignaler
{
    public async Task<bool> SignalAsync(long buildId, LivemapBuildWake wake, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetWorkflowHandle<LivemapBuildWorkflow>(LivemapBuildWorkflow.WorkflowId(buildId))
                .SignalAsync(workflow => workflow.WakeAsync(wake), new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return false;
        }
    }
}
