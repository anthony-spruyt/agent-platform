using System.Globalization;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

public partial class RenovateTriageWorkflow
{
    private const string ClaudePatch = "renovate-triage-claude-v1";
    private const string HeadPatch = "renovate-triage-head-v1";
    private const string PodStep = "claude";
    private const string ContextUnavailable = "context-unavailable";
    private const string BudgetUnavailable = "budget-unavailable";
    private const string NotLiveReason = "not-live";

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

    // A database outage is waited out for two hours before the run parks.
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

    private bool _claude;
    private AgentPodStep? _podStep;
    private StatedVersions _stated = new(null, null);
    private TriageResult? _parsed;
    private string _climbHead = string.Empty;
    private int _seq;

    private sealed record Climb(RenovateTriageInput Input, BeginRunResult? Run, string HeadSha, string Template, string Prompt, string Model, int SignalsSeen);

    private sealed record Attempt(TriageDecision? Decision, bool UsageLimit = false, DateTimeOffset? ResetsAt = null);

    private enum ContextState
    {
        Ready,
        HeadMoved,
        Unavailable,
    }

    [WorkflowUpdate(AgentResultUpdates.SubmitResult)]
    public Task SubmitResultAsync(SubmitResultPayload payload) => _podStep!.AcceptAsync(payload);

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload) => AgentPodStep.Validate(_podStep, payload);

    private static bool WantsAgent(AssessResult assess, TriageDecision? decision) =>
        decision is not null && assess.Mode == TriageMode.Live && !assess.Stored && TriageRules.Climbs(decision);

    // Null means the head moved and the caller reassesses; the step counter carries on in _seq.
    private async Task<RenovateTriageResult?> ClimbAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, string reason, ScannedText? body, TriageMode mode)
    {
        _seq = seq;
        var signalsSeen = _signalCount;
        await ProgressAsync(input, headSha, $"Queued for agent review: {reason}.");

        var stepStarted = Workflow.UtcNow;
        var queued = await Workflow.ExecuteActivityAsync(
            (ClaudeRungActivities activities) => activities.QueueAsync(new QueueInput(input.Repo, input.PrNumber, headSha, reason)),
            _activityOptions);
        await RecordStepAsync(run, ++_seq, "queue", LadderRung.Rule, queued.NotLive ? "not_live" : queued.HeadMoved ? "head_moved" : "queued", stepStarted, queued);
        if (queued.NotLive)
        {
            await FinishCheckAsync(input, headSha, CheckRunConclusion.Skipped, $"Skipped: {NotLiveReason}.");
            await CompleteAsync(run);
            return new RenovateTriageResult(RenovateTriageOutcome.Skipped, headSha, NotLiveReason, null);
        }

        if (queued.HeadMoved)
        {
            return null;
        }

        switch (await BuildClimbContextAsync(input, run, headSha, reason, body))
        {
            case ContextState.HeadMoved:
                return null;
            case ContextState.Unavailable:
                return await ParkAsync(input, run, headSha, ContextUnavailable, null);
        }

        var template = await Workflow.ExecuteActivityAsync(
            (PromptActivities activities) => activities.LoadPromptAsync(AgentPrompts.RenovateTriage),
            _promptOptions);
        var climb = new Climb(input, run, headSha, template, AgentPrompts.Render(template, new AgentRepoContext(input.Repo, input.PrNumber, headSha)), queued.Model, signalsSeen);

        while (true)
        {
            var reservation = await ReserveAsync(climb);
            switch (reservation?.Kind)
            {
                case null:
                    return await ParkAsync(input, run, headSha, BudgetUnavailable, null);
                case ClaudeReservationKind.Park:
                    return await ParkAsync(input, run, headSha, reservation.Cause!, reservation.Until);
                case ClaudeReservationKind.Owner:
                    return await RecordVerdictAsync(input, run, ++_seq, headSha, ClaudeVerdicts.Unavailable(reservation.Cause!), mode, body, null);
            }

            var attempt = await AttemptAsync(climb, reservation);
            if (attempt.Decision is not null)
            {
                return await RecordVerdictAsync(input, run, ++_seq, headSha, attempt.Decision, mode, body, LadderRung.Claude);
            }

            if (attempt.UsageLimit)
            {
                return await ParkAsync(input, run, headSha, ClaudeBudgetCauses.UsageLimit, attempt.ResetsAt);
            }
        }
    }

    private async Task<ContextState> BuildClimbContextAsync(RenovateTriageInput input, BeginRunResult? run, string headSha, string reason, ScannedText? body)
    {
        var stepStarted = Workflow.UtcNow;
        try
        {
            var built = await Workflow.ExecuteActivityAsync(
                (RenovateTriageCodeActivities activities) => activities.BuildContextAsync(new BuildContextInput(input.Repo, input.PrNumber, headSha, reason, body)),
                _scanOptions);
            var stopReason = built.HeadMoved ? "head_moved" : built.Context is null ? "unavailable" : "built";
            await RecordStepAsync(run, ++_seq, "context", LadderRung.Rule, stopReason, stepStarted, new { built.HeadMoved, sections = built.Context?.Sections.Count });
            if (built.HeadMoved)
            {
                return ContextState.HeadMoved;
            }

            if (built.Context is null || built.Stated is null)
            {
                return ContextState.Unavailable;
            }

            _context = built.Context;
            _stated = built.Stated;
            return ContextState.Ready;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogClimbContextFailed(Workflow.Logger, ex);
            await RecordStepAsync(run, ++_seq, "context", LadderRung.Rule, "error", stepStarted, new { error = true });
            return ContextState.Unavailable;
        }
    }

    private async Task<ClaudeReservation?> ReserveAsync(Climb climb)
    {
        var stepStarted = Workflow.UtcNow;
        try
        {
            var reservation = await Workflow.ExecuteActivityAsync(
                (ClaudeBudgetActivities activities) => activities.ReserveAsync(new ReserveClaudeRunInput(Workflow.NewGuid(), climb.Input.Repo, climb.Input.PrNumber, climb.HeadSha, climb.Model)),
                _reserveOptions);
            await RecordStepAsync(climb.Run, ++_seq, "reserve", LadderRung.Rule, reservation.Kind.ToString().ToLowerInvariant(), stepStarted, reservation);
            return reservation;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogReserveFailed(Workflow.Logger, ex);
            await RecordStepAsync(climb.Run, ++_seq, "reserve", LadderRung.Rule, "error", stepStarted, new { error = true });
            return null;
        }
    }

    private async Task<Attempt> AttemptAsync(Climb climb, ClaudeReservation reservation)
    {
        var runId = reservation.RunId!.Value;
        await ProgressAsync(climb.Input, climb.HeadSha, "Waiting for the agent review.");
        await BestEffortAsync(Workflow.ExecuteActivityAsync((ClaudeBudgetActivities activities) => activities.MarkPodCreatedAsync(runId), _budgetWriteOptions));

        _climbHead = climb.HeadSha;
        _parsed = null;
        var podStep = _podStep = new AgentPodStep(PodStep, reservation.Attempt, ParseSubmitted);
        var started = Workflow.UtcNow;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Workflow.CancellationToken);
        var pod = podStep.RunAsync(climb.Prompt, AgentTier.Read, new AgentRepoContext(climb.Input.Repo, climb.Input.PrNumber, climb.HeadSha), AgentRunTimeouts.DefaultPodDeadlineSeconds, stop.Token);
        var watch = Workflow.Patched(HeadPatch) ? WatchHeadAsync(climb, stop.Token) : null;

        AgentPodStepOutcome? outcome = null;
        string? movedTo = null;
        try
        {
            if (watch is not null && await Workflow.WhenAnyAsync(new Task[] { pod, watch }) == watch && watch.IsCompletedSuccessfully)
            {
                movedTo = watch.Result;
                stop.Cancel();
            }

            outcome = await pod;
        }
        catch (Exception ex) when (TemporalException.IsCanceledException(ex))
        {
            if (movedTo is null)
            {
                await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.Cancelled));
                throw;
            }
        }
        catch (ActivityFailureException ex)
        {
            LogPodFailed(Workflow.Logger, ex);
        }
        finally
        {
            stop.Cancel();
            await DrainAsync(watch);
            podStep.Close();
        }

        if (movedTo is null && Workflow.CancellationToken.IsCancellationRequested)
        {
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.Cancelled));
            Workflow.CancellationToken.ThrowIfCancellationRequested();
        }

        if (movedTo is not null)
        {
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.Cancelled));
            await RecordStepAsync(climb.Run, ++_seq, PodStep, LadderRung.Claude, "head_moved", started, new { reservation.Attempt, movedTo });
            await FinishCheckAsync(climb.Input, climb.HeadSha, CheckRunConclusion.Neutral, $"Superseded by {TriageRules.ShortSha(movedTo)}.");
            await CompleteAsync(climb.Run);
            throw Workflow.CreateContinueAsNewException((RenovateTriageWorkflow wf) => wf.RunAsync(climb.Input));
        }

        if (outcome is null)
        {
            await RecordStepAsync(climb.Run, ++_seq, PodStep, LadderRung.Claude, "pod_failed", started, new { reservation.Attempt });
            await FinishAsync(new FinishClaudeRunInput(runId, ClaudeRunOutcome.PodFailed));
            return new Attempt(null);
        }

        var agent = outcome.Agent;
        var succeeded = outcome.Status == AgentPodStepStatus.Succeeded;
        if (climb.Run is not null)
        {
            await podStep.RecordAsync(climb.Run, ++_seq, outcome, started, RunRecorder.ToJson(new { reservation.Attempt, runId }), succeeded ? _parsed : null, AgentPrompts.RenovateTriage, climb.Template);
        }

        var usage = agent.Usage;
        var runOutcome = ToRunOutcome(outcome);
        await FinishAsync(new FinishClaudeRunInput(runId, runOutcome, agent.Model, usage?.InputTokens, usage?.OutputTokens, AgentRunUsage.ToCostUsd(usage?.CostUsd)));
        if (succeeded)
        {
            return new Attempt(ClaudeVerdicts.Decision(_parsed!, agent.Model ?? climb.Model, runId.ToString()));
        }

        if (runOutcome != ClaudeRunOutcome.UsageLimit)
        {
            return new Attempt(null);
        }

        var until = await BestEffortAsync(Workflow.ExecuteActivityAsync(
            (ClaudeBudgetActivities activities) => activities.SetCooldownAsync(new SetClaudeCooldownInput(ClaudeCooldown.Global, agent.ResetsAt, ClaudeBudgetCauses.UsageLimit)),
            _budgetWriteOptions));
        return new Attempt(null, true, until);
    }

    private async Task<string> WatchHeadAsync(Climb climb, CancellationToken cancellationToken)
    {
        var options = new ActivityOptions
        {
            StartToCloseTimeout = _activityOptions.StartToCloseTimeout,
            ScheduleToCloseTimeout = _activityOptions.ScheduleToCloseTimeout,
            RetryPolicy = _activityOptions.RetryPolicy,
            CancellationToken = cancellationToken,
        };
        var seen = climb.SignalsSeen;
        while (true)
        {
            await Workflow.WaitConditionAsync(() => _signalCount > seen, cancellationToken);
            seen = _signalCount;
            try
            {
                var current = await Workflow.ExecuteActivityAsync(
                    (ClaudeRungActivities activities) => activities.ReadHeadAsync(new ReadHeadInput(climb.Input.Repo, climb.Input.PrNumber)),
                    options);
                if (!string.Equals(current, climb.HeadSha, StringComparison.Ordinal))
                {
                    return current;
                }
            }
            catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
            {
                LogHeadReadFailed(Workflow.Logger, ex);
            }
        }
    }

    private static async Task DrainAsync(Task? watch)
    {
        if (watch is null)
        {
            return;
        }

        try
        {
            await watch;
        }
        catch (Exception ex) when (TemporalException.IsCanceledException(ex))
        {
            // The watcher ends by cancellation once the pod is done.
        }
    }

    private async Task<RenovateTriageResult> ParkAsync(RenovateTriageInput input, BeginRunResult? run, string headSha, string cause, DateTimeOffset? until)
    {
        await RecordStepAsync(run, ++_seq, "park", LadderRung.Rule, cause, Workflow.UtcNow, new { cause, until });
        _checkSha = null;
        _checkSummary = null;
        var next = until is { } at ? $", next try after {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC" : string.Empty;
        await ReportCheckAsync(input, headSha, CheckRunStatus.Queued, null, $"Queued for agent review: {cause}{next}.");
        await CompleteAsync(run);
        return new RenovateTriageResult(RenovateTriageOutcome.Parked, headSha, cause, null);
    }

    private string? ParseSubmitted(SubmitResultPayload payload)
    {
        if (!TriageResult.TryParse(payload, _climbHead, _stated, out var parsed, out var error))
        {
            return error;
        }

        _parsed = parsed;
        return null;
    }

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

    private static Task FinishAsync(FinishClaudeRunInput input) =>
        BestEffortAsync(Workflow.ExecuteActivityAsync((ClaudeBudgetActivities activities) => activities.FinishAsync(input), _budgetWriteOptions));

    // The budget books are corrected by the next reservation's window, so a failed write does not stop the triage.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the triage context; the head waits for the next check.")]
    private static partial void LogClimbContextFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reserve an agent run; the head waits for the next check.")]
    private static partial void LogReserveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent pod activity failed; counting it as a failed attempt.")]
    private static partial void LogPodFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the pull request head during the agent run; waiting for the next check.")]
    private static partial void LogHeadReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write to the Claude budget books; the triage carries on.")]
    private static partial void LogBudgetWriteFailed(ILogger logger, Exception exception);
}
