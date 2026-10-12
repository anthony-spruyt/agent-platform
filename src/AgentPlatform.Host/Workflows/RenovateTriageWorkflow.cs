using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public class RenovateTriageWorkflow
{
    public const string CheckSuiteCompletedSignalName = "CheckSuiteCompleted";

    public static readonly TimeSpan CiTimeout = TimeSpan.FromHours(2);

    private static readonly ActivityOptions _activityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 5,
        },
    };

    private CheckSuiteCompletedSignal? _first;
    private int _signalCount;

    public static string WorkflowId(string repo, int prNumber) => $"renovate-triage/{repo}/{prNumber}";

    [WorkflowSignal(CheckSuiteCompletedSignalName)]
    public Task OnCheckSuiteCompletedAsync(CheckSuiteCompletedSignal signal)
    {
        _first ??= signal;
        _signalCount++;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<RenovateTriageResult> RunAsync(RenovateTriageInput? input)
    {
        var trigger = input is null ? "signal" : "new-head";
        if (input is null)
        {
            await Workflow.WaitConditionAsync(() => _first is not null);
            input = new RenovateTriageInput(_first!.Repo, _first.PrNumber);
        }

        var started = Workflow.UtcNow;
        var run = await RunRecorder.BeginAsync(new BeginRunInput(
            nameof(RenovateTriageWorkflow),
            RunRecorder.ToJson(new { repo = input.Repo, prNumber = input.PrNumber, trigger }),
            started));
        try
        {
            return await JudgeAsync(input, run);
        }
        catch (Exception ex) when (ex is ActivityFailureException or OperationCanceledException)
        {
            if (run is not null)
            {
                await RunRecorder.CompleteAfterFailureAsync(run.RunId);
            }

            throw;
        }
    }

    private async Task<RenovateTriageResult> JudgeAsync(RenovateTriageInput input, BeginRunResult? run)
    {
        var seq = 0;
        string? judgedSha = null;
        DateTime? pendingSince = null;
        var timedOut = false;

        while (true)
        {
            var seen = _signalCount;
            var stepStarted = Workflow.UtcNow;
            var assess = await Workflow.ExecuteActivityAsync(
                (RenovateTriageActivities activities) => activities.AssessAsync(new AssessInput(input.Repo, input.PrNumber)),
                _activityOptions);
            await RecordStepAsync(run, ++seq, "assess", LadderRung.Rule, assess.Status.ToString(), stepStarted, assess);

            if (judgedSha is not null && assess.HeadSha != judgedSha)
            {
                await CompleteAsync(run);
                throw Workflow.CreateContinueAsNewException((RenovateTriageWorkflow wf) => wf.RunAsync(input));
            }

            judgedSha = assess.HeadSha;
            TriageDecision? decision = null;
            switch (assess.Status)
            {
                case AssessStatus.Skipped:
                    await CompleteAsync(run);
                    return new RenovateTriageResult(RenovateTriageOutcome.Skipped, assess.HeadSha, assess.Reason, null);
                case AssessStatus.AlreadyJudged:
                    await CompleteAsync(run);
                    return new RenovateTriageResult(RenovateTriageOutcome.AlreadyJudged, assess.HeadSha, null, null);
                case AssessStatus.Pending:
                    pendingSince ??= Workflow.UtcNow;
                    if (!timedOut)
                    {
                        var remaining = pendingSince.Value + CiTimeout - Workflow.UtcNow;
                        if (remaining > TimeSpan.Zero && await Workflow.WaitConditionAsync(() => _signalCount > seen, remaining))
                        {
                            continue;
                        }

                        timedOut = true;
                        continue;
                    }

                    decision = TriageRules.CiTimeoutDecision();
                    break;
                case AssessStatus.Decided:
                    decision = assess.Decision;
                    break;
                default:
                    stepStarted = Workflow.UtcNow;
                    var judge = await Workflow.ExecuteActivityAsync(
                        (RenovateTriageActivities activities) => activities.JudgeAsync(new JudgeInput(input.Repo, input.PrNumber, assess.HeadSha)),
                        _activityOptions);
                    await RecordStepAsync(run, ++seq, "jev", LadderRung.Jev, JudgeStopReason(judge), stepStarted, judge);
                    if (judge.HeadMoved)
                    {
                        continue;
                    }

                    decision = judge.Decision;
                    break;
            }

            return await RecordVerdictAsync(input, run, ++seq, assess.HeadSha, decision!);
        }
    }

    private static string JudgeStopReason(JudgeResult judge) => judge switch
    {
        { HeadMoved: true } => "head_moved",
        { JevError: not null } => "error",
        { Jev: null } => "skipped",
        _ => "answered",
    };

    private static async Task<RenovateTriageResult> RecordVerdictAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, TriageDecision decision)
    {
        var stepStarted = Workflow.UtcNow;
        var recorded = await Workflow.ExecuteActivityAsync(
            (RenovateTriageActivities activities) => activities.RecordVerdictAsync(new RecordVerdictInput(input.Repo, input.PrNumber, headSha, decision)),
            _activityOptions);
        await RecordStepAsync(run, seq, "verdict", LadderRung.Rule, decision.Reason, stepStarted, new { decision, recorded });
        await CompleteAsync(run);
        return new RenovateTriageResult(RenovateTriageOutcome.Judged, headSha, decision.Reason, decision.Verdict);
    }

    private static async Task RecordStepAsync<T>(BeginRunResult? run, int seq, string name, LadderRung rung, string stopReason, DateTime stepStarted, T output)
    {
        if (run is null)
        {
            return;
        }

        await RunRecorder.RecordStepAsync(new RecordStepInput(
            run.RunId,
            seq,
            name,
            rung,
            stopReason,
            stepStarted,
            RunRecorder.ElapsedMs(stepStarted),
            Output: RunRecorder.ToJson(output)));
    }

    private static async Task CompleteAsync(BeginRunResult? run)
    {
        if (run is not null)
        {
            await RunRecorder.CompleteAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Workflow.UtcNow));
        }
    }
}
