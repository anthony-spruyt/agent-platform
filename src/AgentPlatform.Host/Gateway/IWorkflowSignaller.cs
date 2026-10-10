using AgentPlatform.Host.Agents;

namespace AgentPlatform.Host.Gateway;

public interface IWorkflowSignaller
{
    Task SignalResultAsync(AgentRunIdentity run, SubmitResultPayload payload, CancellationToken cancellationToken);
}

public sealed class RunNotActiveException : Exception
{
    public RunNotActiveException(string workflowId)
        : base($"Workflow {workflowId} is no longer accepting results.")
    {
    }
}
