using AgentPlatform.Host.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public interface ITriageWorkflowProbe
{
    Task<bool> IsRunningAsync(string repo, int prNumber, CancellationToken cancellationToken);
}

public sealed class TemporalTriageWorkflowProbe(ITemporalClient client) : ITriageWorkflowProbe
{
    public async Task<bool> IsRunningAsync(string repo, int prNumber, CancellationToken cancellationToken)
    {
        var handle = client.GetWorkflowHandle(RenovateTriageWorkflow.WorkflowId(repo, prNumber));
        try
        {
            var description = await handle.DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
            return description.Status == WorkflowExecutionStatus.Running;
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return false;
        }
    }
}
