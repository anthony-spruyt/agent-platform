using AgentPlatform.Host.Workflows;
using Temporalio.Client;

namespace AgentPlatform.Host.GitHub;

public interface IRenovateTriageSignaler
{
    Task SignalAsync(CheckSuiteCompletedSignal signal, CancellationToken cancellationToken);
}

public sealed class TemporalRenovateTriageSignaler(ITemporalClient client) : IRenovateTriageSignaler
{
    public async Task SignalAsync(CheckSuiteCompletedSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        await client.StartWorkflowAsync(
            (RenovateTriageWorkflow workflow) => workflow.RunAsync(),
            new WorkflowOptions(RenovateTriageWorkflow.WorkflowId(signal.Repo, signal.PrNumber), TaskQueues.AgentPlatform)
            {
                StartSignal = RenovateTriageWorkflow.CheckSuiteCompletedSignalName,
                StartSignalArgs = [signal],
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            }).ConfigureAwait(false);
    }
}
