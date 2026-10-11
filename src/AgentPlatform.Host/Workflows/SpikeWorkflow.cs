using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RunData;
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
        var info = Workflow.Info;
        var started = Workflow.UtcNow;
        var run = await RunRecorder.BeginAsync(new BeginRunInput(
            nameof(SpikeWorkflow),
            RunRecorder.ToJson(input),
            started,
            PromptName: AgentStep,
            PromptBody: input.Prompt));

        try
        {
            var agentStarted = Workflow.UtcNow;
            var agent = await RunAgentPodAsync(input);
            await DeletePodAsync();

            var status = SpikeStatus.AgentFailed;
            if (agent.Outcome == AgentRunOutcome.Succeeded)
            {
                var arrived = await Workflow.WaitConditionAsync(() => _result is not null, ResultTimeout);
                status = arrived ? SpikeStatus.Succeeded : SpikeStatus.ResultTimeout;
            }

            var usage = agent.Usage;
            await RunRecorder.RecordStepAsync(new RecordStepInput(
                run.RunId,
                1,
                AgentStep,
                LadderRung.Claude,
                StopReason(status, agent.Outcome),
                agentStarted,
                RunRecorder.ElapsedMs(agentStarted),
                Input: RunRecorder.ToJson(new { tier = input.Tier }),
                Output: RunRecorder.ToJson(new { outcome = agent.Outcome, resultText = agent.ResultText, detail = agent.Detail, submitted = _result }),
                PromptHash: run.PromptHash,
                Model: agent.Model,
                InputTokens: usage?.InputTokens,
                OutputTokens: usage?.OutputTokens,
                CostUsd: usage?.CostUsd is { } cost ? (decimal)cost : null));
            await RunRecorder.CompleteAsync(new CompleteRunInput(
                run.RunId,
                status == SpikeStatus.Succeeded ? RunStatus.Succeeded : RunStatus.Failed,
                Workflow.UtcNow,
                agent.Model));

            return new SpikeResult(status, _result, agent, input.Tier, info.WorkflowId, info.RunId);
        }
        catch (Exception ex) when (ex is ActivityFailureException or OperationCanceledException)
        {
            await RunRecorder.CompleteAfterFailureAsync(run.RunId);
            throw;
        }
    }

    private static string StopReason(SpikeStatus status, AgentRunOutcome outcome) => status switch
    {
        SpikeStatus.Succeeded => "submitted",
        SpikeStatus.ResultTimeout => "result_timeout",
        _ => JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString()),
    };

    private static async Task<AgentRunResult> RunAgentPodAsync(SpikeInput input)
    {
        var deadline = AgentRunTimeouts.DefaultPodDeadlineSeconds;
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (AgentPodActivities activities) => activities.RunAgentPodAsync(new RunAgentPodInput(AgentStep, input.Prompt, input.Tier, deadline)),
                new ActivityOptions
                {
                    TaskQueue = TaskQueues.AgentPods,
                    StartToCloseTimeout = AgentRunTimeouts.ActivityStartToClose(deadline),
                    ScheduleToCloseTimeout = AgentRunTimeouts.ActivityScheduleToClose,
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
