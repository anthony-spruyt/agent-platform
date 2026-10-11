using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Gateway;

public sealed class TemporalWorkflowResultSubmitter(ITemporalClient client) : IWorkflowResultSubmitter
{
    public async Task SubmitResultAsync(AgentRunIdentity run, SubmitResultPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(payload);

        var handle = client.GetWorkflowHandle(run.WorkflowId, run.RunId);
        try
        {
            await handle.ExecuteUpdateAsync(
                SpikeWorkflow.SubmitResultUpdate,
                [payload],
                new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (WorkflowUpdateFailedException ex) when (ex.InnerException is ApplicationFailureException { ErrorType: SpikeWorkflow.ResultAlreadySubmittedErrorType })
        {
            throw new ResultAlreadySubmittedException(run.WorkflowId);
        }
        catch (RpcException ex) when (ex.Code is RpcException.StatusCode.NotFound or RpcException.StatusCode.FailedPrecondition)
        {
            throw new RunNotActiveException(run.WorkflowId);
        }
    }
}
