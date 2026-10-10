using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Gateway;

public sealed class TemporalWorkflowSignaller(ITemporalClient client) : IWorkflowSignaller
{
    public async Task SignalResultAsync(AgentRunIdentity run, SubmitResultPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);

        var handle = client.GetWorkflowHandle(run.WorkflowId, run.RunId);
        try
        {
            await handle.SignalAsync(
                SpikeWorkflow.SubmitResultSignal,
                [payload],
                new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code is RpcException.StatusCode.NotFound or RpcException.StatusCode.FailedPrecondition)
        {
            throw new RunNotActiveException(run.WorkflowId);
        }
    }
}
