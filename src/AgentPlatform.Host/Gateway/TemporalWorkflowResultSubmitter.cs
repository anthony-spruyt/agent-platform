using AgentPlatform.Host.Agents;
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
                AgentResultUpdates.SubmitResult,
                [payload],
                new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (WorkflowUpdateFailedException ex) when (Map(ex, run) is not null)
        {
            throw Map(ex, run)!;
        }
        catch (RpcException ex) when (ex.Code is RpcException.StatusCode.NotFound or RpcException.StatusCode.FailedPrecondition)
        {
            throw new RunNotActiveException(run.WorkflowId);
        }
    }

    private static Exception? Map(WorkflowUpdateFailedException ex, AgentRunIdentity run) =>
        ex.InnerException is ApplicationFailureException failure
            ? failure.ErrorType switch
            {
                AgentResultUpdates.ResultAlreadySubmittedErrorType => new ResultAlreadySubmittedException(run.WorkflowId),
                AgentResultUpdates.InvalidResultErrorType => new InvalidResultException(failure.Message),
                AgentResultUpdates.StaleRunErrorType => new RunNotActiveException(run.WorkflowId),
                _ => null,
            }
            : null;
}
