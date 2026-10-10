using System.Text.Json.Serialization;
using AgentPlatform.Host.Agents;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

public sealed record SpikeInput(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("tier")] AgentTier Tier);

[JsonConverter(typeof(JsonStringEnumConverter<SpikeStatus>))]
public enum SpikeStatus
{
    Succeeded,
    AgentFailed,
    ResultTimeout,
}

public sealed record SpikeResult(
    SpikeStatus Status,
    SubmitResultPayload? Result,
    AgentRunResult Agent,
    AgentTier Tier,
    string WorkflowId,
    string RunId);

[Workflow]
public partial class SpikeWorkflow
{
    public const string SubmitResultUpdate = "submit_result";
    public const string ResultAlreadySubmittedErrorType = "ResultAlreadySubmitted";

    public static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(2);

    private const string AgentStep = "spike";

    private SubmitResultPayload? _result;

    [WorkflowUpdate(SubmitResultUpdate)]
    public Task SubmitResultAsync(SubmitResultPayload payload)
    {
        _result = payload;
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload)
    {
        if (_result is not null)
        {
            throw new ApplicationFailureException(
                "A result was already submitted for this run.",
                errorType: ResultAlreadySubmittedErrorType,
                nonRetryable: true);
        }
    }

    [WorkflowRun]
    public async Task<SpikeResult> RunAsync(SpikeInput input)
    {
        var deadline = AgentRunTimeouts.DefaultPodDeadlineSeconds;
        AgentRunResult agent;
        try
        {
            agent = await Workflow.ExecuteActivityAsync(
                (AgentPodActivities activities) => activities.RunAgentPodAsync(new RunAgentPodInput(AgentStep, input.Prompt, input.Tier, deadline)),
                new ActivityOptions
                {
                    TaskQueue = TaskQueues.AgentPods,
                    StartToCloseTimeout = AgentRunTimeouts.ActivityStartToClose(deadline),
                    HeartbeatTimeout = AgentRunTimeouts.HeartbeatTimeout,
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                    RetryPolicy = new RetryPolicy
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        BackoffCoefficient = 2,
                        MaximumInterval = TimeSpan.FromMinutes(1),
                        MaximumAttempts = 5,
                    },
                });
        }
        catch (Exception ex) when (ex is ActivityFailureException or OperationCanceledException)
        {
            await DeletePodAsync();
            throw;
        }

        await DeletePodAsync();

        var info = Workflow.Info;
        if (agent.Outcome != AgentRunOutcome.Succeeded)
        {
            return new SpikeResult(SpikeStatus.AgentFailed, _result, agent, input.Tier, info.WorkflowId, info.RunId);
        }

        var arrived = await Workflow.WaitConditionAsync(() => _result is not null, ResultTimeout);
        return new SpikeResult(
            arrived ? SpikeStatus.Succeeded : SpikeStatus.ResultTimeout,
            _result,
            agent,
            input.Tier,
            info.WorkflowId,
            info.RunId);
    }

    private static async Task DeletePodAsync()
    {
        try
        {
            await Workflow.ExecuteLocalActivityAsync(
                (AgentPodActivities activities) => activities.DeleteAgentPodAsync(AgentStep),
                new LocalActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(30),
                    CancellationToken = CancellationToken.None,
                    RetryPolicy = new RetryPolicy { InitialInterval = TimeSpan.FromSeconds(1), MaximumAttempts = 5 },
                });
        }
        catch (ActivityFailureException ex)
        {
            LogPodCleanupFailed(Workflow.Logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the agent pod after the run.")]
    private static partial void LogPodCleanupFailed(ILogger logger, Exception exception);
}
