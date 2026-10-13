using AgentPlatform.Host.Agents;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Workflows;

public sealed record RunAgentPodInput(string Step, string Prompt, AgentTier Tier, int DeadlineSeconds, int Attempt = 1, AgentRepoContext? Repo = null);

public sealed partial class AgentPodActivities(IAgentRunner runner, AgentPodsOptions options, ILogger<AgentPodActivities> logger)
{
    public const string RunAgentPodName = "RunAgentPod";
    public const string DeleteAgentPodName = "DeleteAgentPod";

    [Activity(RunAgentPodName)]
    public async Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DeadlineSeconds is < 1 or > AgentPodSpecBuilder.MaxDeadlineSeconds)
        {
            throw new ApplicationFailureException(
                $"The pod deadline must be between 1 and {AgentPodSpecBuilder.MaxDeadlineSeconds} seconds.",
                nonRetryable: true);
        }

        var context = ActivityExecutionContext.Current;
        var request = new AgentRunRequest(
            context.Info.WorkflowId ?? throw new ApplicationFailureException("RunAgentPod must be started by a workflow.", nonRetryable: true),
            context.Info.WorkflowRunId ?? throw new ApplicationFailureException("RunAgentPod must be started by a workflow.", nonRetryable: true),
            input.Step,
            input.Prompt,
            input.Tier,
            input.DeadlineSeconds,
            input.Attempt,
            input.Repo);

        using var heartbeatTimer = new PeriodicTimer(options.HeartbeatInterval);
        var heartbeats = HeartbeatAsync(context, heartbeatTimer);
        try
        {
            return await runner.RunAsync(request, context.CancellationToken).ConfigureAwait(false);
        }
        catch (AgentConfigurationException ex)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
        catch (OperationCanceledException) when (context.CancelReason == ActivityCancelReason.CancelRequested)
        {
            LogCancelling(logger, request.WorkflowId);
            await runner.CancelAsync(request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            heartbeatTimer.Dispose();
            await heartbeats.ConfigureAwait(false);
        }
    }

    [Activity(DeleteAgentPodName)]
    public Task DeleteAgentPodAsync(string step)
    {
        var context = ActivityExecutionContext.Current;
        var request = new AgentRunRequest(
            context.Info.WorkflowId ?? throw new ApplicationFailureException("DeleteAgentPod must be started by a workflow.", nonRetryable: true),
            context.Info.WorkflowRunId ?? throw new ApplicationFailureException("DeleteAgentPod must be started by a workflow.", nonRetryable: true),
            step,
            string.Empty,
            AgentTier.Read,
            1);
        return runner.CancelAsync(request, context.CancellationToken);
    }

    private static async Task HeartbeatAsync(ActivityExecutionContext context, PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            context.Heartbeat();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow {WorkflowId} cancelled the activity; deleting its agent pod.")]
    private static partial void LogCancelling(ILogger logger, string workflowId);
}
