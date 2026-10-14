using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

public enum AgentPodStepStatus
{
    Succeeded,
    AgentFailed,
    ResultTimeout,
    InvalidResult,
}

public sealed record AgentPodStepOutcome(AgentPodStepStatus Status, AgentRunResult Agent);

public sealed partial class AgentPodStep(string step, int attempt, Func<SubmitResultPayload, string?> parse)
{
    private const int MaxInvalidSubmits = 2;

    public static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(2);

    private bool _submitted;
    private bool _closed;
    private int _invalidSubmits;

    public string Step { get; } = step;

    public int Attempt { get; } = attempt;

    public static void Validate(AgentPodStep? current, SubmitResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (current is null || current._closed || payload.Step != current.Step || payload.Attempt != current.Attempt)
        {
            throw new ApplicationFailureException(
                "The result does not belong to the current run step.",
                errorType: AgentResultUpdates.StaleRunErrorType,
                nonRetryable: true);
        }

        if (current._submitted)
        {
            throw new ApplicationFailureException(
                "A result was already submitted for this run.",
                errorType: AgentResultUpdates.ResultAlreadySubmittedErrorType,
                nonRetryable: true);
        }

        if (current._invalidSubmits >= MaxInvalidSubmits)
        {
            throw new ApplicationFailureException(
                "The run already failed after repeated invalid results.",
                errorType: AgentResultUpdates.InvalidResultErrorType,
                nonRetryable: true);
        }
    }

    public void Close() => _closed = true;

    public Task AcceptAsync(SubmitResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (parse(payload) is { } error)
        {
            _invalidSubmits++;
            throw new ApplicationFailureException(error, errorType: AgentResultUpdates.InvalidResultErrorType, nonRetryable: true);
        }

        _submitted = true;
        return Task.CompletedTask;
    }

    private static string StopReason(AgentPodStepOutcome outcome) => outcome.Status switch
    {
        AgentPodStepStatus.Succeeded => "submitted",
        AgentPodStepStatus.ResultTimeout => "result_timeout",
        AgentPodStepStatus.InvalidResult => "invalid_result",
        _ => JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.Agent.Outcome.ToString()),
    };

    public async Task<AgentPodStepOutcome> RunAsync(
        string prompt,
        AgentTier tier,
        AgentRepoContext? repo,
        int deadlineSeconds,
        CancellationToken cancellationToken)
    {
        var agent = await RunPodAsync(prompt, tier, repo, deadlineSeconds, cancellationToken);
        await DeletePodAsync();

        var status = AgentPodStepStatus.AgentFailed;
        if (agent.Outcome == AgentRunOutcome.Succeeded)
        {
            var arrived = await Workflow.WaitConditionAsync(() => _submitted || _invalidSubmits >= MaxInvalidSubmits, ResultTimeout, cancellationToken);
            status = !arrived ? AgentPodStepStatus.ResultTimeout : _submitted ? AgentPodStepStatus.Succeeded : AgentPodStepStatus.InvalidResult;
        }

        Close();
        return new AgentPodStepOutcome(status, agent);
    }

    public Task RecordAsync(BeginRunResult run, int seq, AgentPodStepOutcome outcome, DateTime started, JsonElement? input, object? submitted)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcome);
        var agent = outcome.Agent;
        var usage = agent.Usage;
        return RunRecorder.RecordStepAsync(new RecordStepInput(
            run.RunId,
            seq,
            Step,
            LadderRung.Claude,
            StopReason(outcome),
            started,
            RunRecorder.ElapsedMs(started),
            Input: input,
            Output: RunRecorder.ToJson(new { outcome = agent.Outcome, resultText = agent.ResultText, detail = agent.Detail, submitted }),
            PromptHash: run.PromptHash,
            Model: agent.Model,
            InputTokens: usage?.InputTokens,
            OutputTokens: usage?.OutputTokens,
            CostUsd: AgentRunUsage.ToCostUsd(usage?.CostUsd)));
    }

    private async Task<AgentRunResult> RunPodAsync(string prompt, AgentTier tier, AgentRepoContext? repo, int deadline, CancellationToken cancellationToken)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (AgentPodActivities activities) => activities.RunAgentPodAsync(new RunAgentPodInput(Step, prompt, tier, deadline, Attempt, repo)),
                new ActivityOptions
                {
                    TaskQueue = TaskQueues.AgentPods,
                    StartToCloseTimeout = AgentRunTimeouts.ActivityStartToClose(deadline),
                    ScheduleToCloseTimeout = AgentRunTimeouts.ActivityScheduleToClose,
                    HeartbeatTimeout = AgentRunTimeouts.HeartbeatTimeout,
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                    CancellationToken = cancellationToken,
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

    private async Task DeletePodAsync()
    {
        try
        {
            await Workflow.ExecuteLocalActivityAsync(
                (AgentPodActivities activities) => activities.DeleteAgentPodAsync(Step),
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
