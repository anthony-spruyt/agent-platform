using AgentPlatform.Host.Agents;

namespace AgentPlatform.Host.Gateway;

public interface IWorkflowResultSubmitter
{
    Task SubmitResultAsync(AgentRunIdentity run, SubmitResultPayload payload, CancellationToken cancellationToken);
}

public sealed class ResultAlreadySubmittedException : Exception
{
    public ResultAlreadySubmittedException(string workflowId)
        : base($"Workflow {workflowId} already has a submitted result.")
    {
    }
}

public sealed class RunNotActiveException : Exception
{
    public RunNotActiveException(string workflowId)
        : base($"Workflow {workflowId} is no longer accepting results.")
    {
    }
}

public sealed class InvalidResultException(string reason) : Exception($"The result was rejected: {reason}");
