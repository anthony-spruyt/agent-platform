using AgentPlatform.Host.Agents;
using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Livemap;

public partial class LivemapBuildWorkflow
{
    // A patching build only appears after this patch, so a run recorded before it replays without it.
    private const string HardPathPatch = "livemap-hard-path";

    // A pod may wait this long for a node before its own deadline starts counting.
    private static readonly TimeSpan _queueWait = TimeSpan.FromMinutes(60);

    private static readonly ActivityOptions _promptOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(30),
        },
    };

    // A database outage is waited out for two hours before the build parks.
    private static readonly ActivityOptions _reserveOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMinutes(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(5),
        },
    };

    private static readonly ActivityOptions _budgetWriteOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 5,
        },
        CancellationToken = CancellationToken.None,
    };

    private AgentPodStep? _podStep;
    private LivemapResult? _parsed;
    private string? _parseError;

    [WorkflowUpdate(AgentResultUpdates.SubmitResult)]
    public Task SubmitResultAsync(SubmitResultPayload payload) => _podStep!.AcceptAsync(payload);

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload) => AgentPodStep.Validate(_podStep, payload);

    private static ClaudeRunOutcome ToRunOutcome(AgentPodStepOutcome outcome) => outcome.Status switch
    {
        AgentPodStepStatus.Succeeded => ClaudeRunOutcome.Succeeded,
        AgentPodStepStatus.ResultTimeout => ClaudeRunOutcome.NoResult,
        AgentPodStepStatus.InvalidResult => ClaudeRunOutcome.InvalidResult,
        _ => outcome.Agent.Outcome switch
        {
            AgentRunOutcome.UsageLimit => ClaudeRunOutcome.UsageLimit,
            AgentRunOutcome.TimedOut => ClaudeRunOutcome.TimedOut,
            AgentRunOutcome.Truncated => ClaudeRunOutcome.Truncated,
            AgentRunOutcome.LimitExceeded => ClaudeRunOutcome.LimitStop,
            _ => ClaudeRunOutcome.PodFailed,
        },
    };

    // One agent run per call: a run that leaves the build patching is followed by the next turn's run, and the run cap is checked when a run is prepared.
    private async Task PatchAsync(LivemapBuildInput input)
    {
        _step = "patch prepare";
        var template = await Workflow.ExecuteActivityAsync(
            (PromptActivities activities) => activities.LoadPromptAsync(AgentPrompts.LivemapHardPatch),
            _promptOptions);
        var start = await Workflow.ExecuteActivityAsync(
            (LivemapPatchActivities activities) => activities.PrepareAsync(new LivemapPatchPrepareInput(input.BuildId, template)),
            _readOptions);
        if (start.Kind != LivemapPatchStartKind.Ready)
        {
            return;
        }

        _step = "patch budget";
        var reservation = await Workflow.ExecuteActivityAsync(
            (ClaudeBudgetActivities activities) => activities.ReserveAsync(LivemapBudget.Reserve(Workflow.NewGuid(), input.BuildId, start.Fork, start.MainSha, start.Model)),
            _reserveOptions);
        switch (reservation.Kind)
        {
            case ClaudeReservationKind.Park:
                await ParkPatchAsync(input.BuildId, $"The agent budget is used up ({reservation.Cause}).", reservation.Until ?? Workflow.UtcNow + LivemapBuildActivities.RetryAfter, refundRun: false);
                return;
            case ClaudeReservationKind.Owner:
                await Workflow.ExecuteActivityAsync(
                    (LivemapPatchActivities activities) => activities.NeedsPersonAsync(new LivemapParkInput(input.BuildId, $"The agent budget gives this build no more runs ({reservation.Cause}); it needs a person.")),
                    _readOptions);
                return;
        }

        await RunAgentAsync(input, start, reservation);
    }

    private async Task RunAgentAsync(LivemapBuildInput input, LivemapPatchStart start, ClaudeReservation reservation)
    {
        var runId = reservation.RunId!.Value;
        _step = "patch run";
        var began = await Workflow.ExecuteActivityAsync(
            (LivemapPatchActivities activities) => activities.BeginAsync(new LivemapPatchBeginInput(input.BuildId, start.Run)),
            _stepOptions);
        if (!began)
        {
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.NotStarted));
            return;
        }

        await BestEffortAsync(Workflow.ExecuteActivityAsync((ClaudeBudgetActivities activities) => activities.MarkPodCreatedAsync(runId), _budgetWriteOptions));
        var podStep = _podStep = new AgentPodStep($"claude-{start.Run}", reservation.Attempt, ParseSubmitted);
        AgentPodStepOutcome outcome;
        try
        {
            outcome = await podStep.RunAsync(
                start.Prompt,
                AgentTier.Read,
                new AgentRepoContext(start.Fork, null, start.MainSha),
                start.DeadlineSeconds,
                Workflow.CancellationToken,
                start.Overrides,
                AgentRunTimeouts.ActivityStartToClose(start.DeadlineSeconds) + _queueWait);
        }
        catch (Exception ex) when (TemporalException.IsCanceledException(ex))
        {
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.Cancelled));
            throw;
        }
        catch (ActivityFailureException ex)
        {
            LogPodFailed(Workflow.Logger, ex);
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.PodFailed));
            await ParkAsync(input.BuildId, "The agent pod did not finish: the step 'patch run' failed after its retries.");
            return;
        }
        finally
        {
            podStep.Close();
        }

        if (Workflow.CancellationToken.IsCancellationRequested)
        {
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.Cancelled));
            Workflow.CancellationToken.ThrowIfCancellationRequested();
        }

        var agent = outcome.Agent;
        var usage = agent.Usage;
        var runOutcome = ToRunOutcome(outcome);
        await FinishAsync(new FinishClaudeRunInput(runId, runOutcome, agent.Model, usage?.InputTokens, usage?.OutputTokens, AgentRunUsage.ToCostUsd(usage?.CostUsd)));
        if (runOutcome == ClaudeRunOutcome.UsageLimit)
        {
            var until = await BestEffortAsync(Workflow.ExecuteActivityAsync(
                (ClaudeBudgetActivities activities) => activities.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, agent.ResetsAt, ClaudeBudgetCauses.UsageLimit)),
                _budgetWriteOptions));
            await ParkPatchAsync(input.BuildId, "The agent hit its usage limit; the run is tried again after the reset.", until ?? Workflow.UtcNow + LivemapBuildActivities.RetryAfter, refundRun: true);
            return;
        }

        var accepted = new LivemapPatchAcceptInput(input.BuildId, start.Run, start.MainSha, outcome.Status == AgentPodStepStatus.Succeeded ? _parsed : null, Failure(outcome));
        await Workflow.ExecuteActivityAsync((LivemapPatchActivities activities) => activities.AcceptAsync(accepted), _stepOptions);
    }

    private string? Failure(AgentPodStepOutcome outcome) => outcome.Status switch
    {
        AgentPodStepStatus.Succeeded => null,
        AgentPodStepStatus.ResultTimeout => "the agent ended without submitting a result",
        AgentPodStepStatus.InvalidResult => $"the agent's results were refused: {_parseError}",
        _ => $"the pod ended with {outcome.Agent.Outcome}",
    };

    private string? ParseSubmitted(SubmitResultPayload payload)
    {
        if (!LivemapResult.TryParse(payload, out var parsed, out var error))
        {
            _parseError = error;
            return error;
        }

        _parsed = parsed;
        return null;
    }

    private static Task<LivemapBuildOutcome> ParkPatchAsync(long buildId, string detail, DateTimeOffset until, bool refundRun) =>
        Workflow.ExecuteActivityAsync(
            (LivemapPatchActivities activities) => activities.ParkAsync(new LivemapPatchParkInput(buildId, detail, until, refundRun)),
            _readOptions);

    private static Task FinishAsync(FinishClaudeRunInput input) =>
        BestEffortAsync(Workflow.ExecuteActivityAsync((ClaudeBudgetActivities activities) => activities.FinishAsync(input), _budgetWriteOptions));

    // The budget books are corrected by the next reservation's window, so a failed write does not stop the build.
    private static async Task BestEffortAsync(Task activity)
    {
        try
        {
            await activity;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogBudgetWriteFailed(Workflow.Logger, ex);
        }
    }

    private static async Task<DateTimeOffset?> BestEffortAsync(Task<DateTimeOffset> activity)
    {
        try
        {
            return await activity;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogBudgetWriteFailed(Workflow.Logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent pod activity failed; the build is parked.")]
    private static partial void LogPodFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write to the Claude budget books; the build carries on.")]
    private static partial void LogBudgetWriteFailed(ILogger logger, Exception exception);
}
