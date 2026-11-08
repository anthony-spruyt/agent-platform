using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Livemap;

[Workflow]
public class LivemapPollWorkflow
{
    public const string WorkflowId = "livemap-poll-run";

    private static readonly ActivityOptions _activityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(10),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 3,
        },
    };

    [WorkflowRun]
    public Task<LivemapPollResult> RunAsync() =>
        Workflow.ExecuteActivityAsync((LivemapPollActivities activities) => activities.PollAsync(), _activityOptions);
}
