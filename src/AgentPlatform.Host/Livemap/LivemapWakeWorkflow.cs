using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Livemap;

[Workflow]
public class LivemapWakeWorkflow
{
    private static readonly ActivityOptions _activityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3,
        },
    };

    public static string WorkflowId(long suiteId) => $"livemap-wake-{suiteId}";

    [WorkflowRun]
    public Task<LivemapWakeOutcome> RunAsync(LivemapBuildWake wake) =>
        Workflow.ExecuteActivityAsync((LivemapWakeActivities activities) => activities.WakeAsync(wake), _activityOptions);
}
