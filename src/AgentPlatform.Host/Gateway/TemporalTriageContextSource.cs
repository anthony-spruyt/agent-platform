using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RenovateTriage;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Gateway;

public sealed class TemporalTriageContextSource(ITemporalClient client) : ITriageContextSource
{
    public async Task<TriageContext?> GetAsync(AgentRunIdentity run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var handle = client.GetWorkflowHandle(run.WorkflowId, run.RunId);
        try
        {
            return await handle.QueryAsync<TriageContext?>(
                TriageContextQueries.GetTriageContext,
                [],
                new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (WorkflowQueryFailedException)
        {
            return null;
        }
        catch (RpcException ex) when (ex.Code is RpcException.StatusCode.NotFound or RpcException.StatusCode.FailedPrecondition)
        {
            throw new RunNotActiveException(run.WorkflowId);
        }
    }
}
