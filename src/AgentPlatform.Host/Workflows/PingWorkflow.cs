using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public class PingWorkflow
{
    [WorkflowRun]
    public Task<string> RunAsync(string name) =>
        Workflow.ExecuteActivityAsync(
            (PingActivities activities) => activities.Ping(name),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
}
