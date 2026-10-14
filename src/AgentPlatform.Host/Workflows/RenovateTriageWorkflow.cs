using System.Text.Json;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public partial class RenovateTriageWorkflow
{
    public const string CheckSuiteCompletedSignalName = "CheckSuiteCompleted";

    private const string JevRetryPatch = "renovate-triage-jev-retry-v1";
    private const string LivePatch = "renovate-triage-live-v1";
    private const string Step2Patch = "renovate-triage-step2-v1";

    public static readonly TimeSpan CiTimeout = TimeSpan.FromHours(2);

    // The scanner takes up to 10 minutes per text and texts go through it one at a time.
    internal static readonly ActivityOptions ScanOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(1),
        ScheduleToCloseTimeout = TimeSpan.FromHours(3),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(30),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(5),
            MaximumAttempts = 3,
        },
    };

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

    private static readonly ActivityOptions _judgeOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(3),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMinutes(5),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(10),
            MaximumAttempts = 3,
        },
    };

    private static readonly ActivityOptions _applyOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(7),
        ScheduleToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(10),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(5),
            MaximumAttempts = 12,
        },
    };

    private CheckSuiteCompletedSignal? _first;
    private int _signalCount;
    private bool _step2;
    private TriageMode _mode = TriageMode.Shadow;
    private TriageContext? _context;
    private string? _checkSha;
    private string? _checkSummary;

    public static string WorkflowId(string repo, int prNumber) => $"renovate-triage/{repo}/{prNumber}";

    [WorkflowSignal(CheckSuiteCompletedSignalName)]
    public Task OnCheckSuiteCompletedAsync(CheckSuiteCompletedSignal signal)
    {
        _first ??= signal;
        _signalCount++;
        return Task.CompletedTask;
    }

    [WorkflowQuery(TriageContextQueries.GetTriageContext)]
    public TriageContext? GetTriageContext() => _context;

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
        _step2 = Workflow.Patched(Step2Patch);
        _claude = Workflow.Patched(ClaudePatch);
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
        finally
        {
            await CloseCheckAsync(input);
        }
    }

    private async Task<RenovateTriageResult> JudgeAsync(RenovateTriageInput input, BeginRunResult? run)
    {
        var seq = 0;
        string? judgedSha = null;
        string? precheckedSha = null;
        ScannedText? body = null;
        var ciWait = CiWait.Initial;
        var reran = false;

        while (true)
        {
            var seen = _signalCount;
            var stepStarted = Workflow.UtcNow;
            var assess = await Workflow.ExecuteActivityAsync(
                (RenovateTriageActivities activities) => activities.AssessAsync(new AssessInput(input.Repo, input.PrNumber, _step2, precheckedSha, _claude)),
                _activityOptions);
            await RecordStepAsync(run, ++seq, "assess", LadderRung.Rule, assess.Status.ToString(), stepStarted, assess);
            _mode = assess.Mode;

            if (judgedSha is not null && assess.HeadSha != judgedSha)
            {
                await FinishCheckAsync(input, judgedSha, CheckRunConclusion.Neutral, $"Superseded by {TriageRules.ShortSha(assess.HeadSha)}.");
                await CompleteAsync(run);
                throw Workflow.CreateContinueAsNewException((RenovateTriageWorkflow wf) => wf.RunAsync(input));
            }

            judgedSha = assess.HeadSha;
            TriageDecision? decision = null;
            string? climbReason = null;
            switch (assess.Status)
            {
                case AssessStatus.Climb:
                    climbReason = assess.Reason;
                    break;
                case AssessStatus.Skipped:
                    await FinishCheckAsync(input, assess.HeadSha, CheckRunConclusion.Skipped, $"Skipped: {assess.Reason}.");
                    await CompleteAsync(run);
                    return new RenovateTriageResult(RenovateTriageOutcome.Skipped, assess.HeadSha, assess.Reason, null);
                case AssessStatus.AlreadyJudged:
                    await FinishCheckAsync(input, assess.HeadSha, CheckRunConclusion.Skipped, "Skipped: this commit already has a verdict.");
                    await CompleteAsync(run);
                    return new RenovateTriageResult(RenovateTriageOutcome.AlreadyJudged, assess.HeadSha, null, null);
                case AssessStatus.NeedsPrecheck:
                    await ProgressAsync(input, assess.HeadSha, "Checking the diff and the PR text.");
                    var precheck = await PrecheckAsync(input, run, ++seq, assess.HeadSha, "precheck");
                    if (precheck.HeadMoved)
                    {
                        continue;
                    }

                    if (precheck.Verdict != PrecheckVerdict.Passed)
                    {
                        decision = TriageRules.PrecheckDecision(precheck.Verdict);
                        break;
                    }

                    precheckedSha = assess.HeadSha;
                    body = precheck.Body;
                    stepStarted = Workflow.UtcNow;
                    var migration = await Workflow.ExecuteActivityAsync(
                        (RenovateTriageCodeActivities activities) => activities.DetectMigrationAsync(new DetectMigrationInput(input.Repo, input.PrNumber, assess.HeadSha, body!)),
                        _activityOptions);
                    await RecordStepAsync(run, ++seq, "migration", LadderRung.Rule, migration.HeadMoved ? "head_moved" : migration.Decision is null ? "none" : "found", stepStarted, migration);
                    if (migration.Decision is not null && !migration.HeadMoved)
                    {
                        decision = migration.Decision;
                        break;
                    }

                    continue;
                case AssessStatus.Pending:
                    await ProgressAsync(input, assess.HeadSha, "Waiting for CI.");
                    ciWait = ciWait.Pending(Workflow.UtcNow);
                    if (!ciWait.TimedOut)
                    {
                        ciWait = await WaitForCiSignalAsync(ciWait, seen);
                        continue;
                    }

                    decision = TriageRules.CiTimeoutDecision();
                    break;
                case AssessStatus.CiFailed:
                    await ProgressAsync(input, assess.HeadSha, "Looking at the failed CI jobs.");
                    var rerun = await ClassifyAndRerunAsync(input, run, ++seq, assess.HeadSha, assess.Mode, reran);
                    seq = rerun.Seq;
                    if (rerun.HeadMoved)
                    {
                        continue;
                    }

                    if (rerun.Rerun)
                    {
                        reran = true;
                        ciWait = ciWait.AfterRerun().Pending(Workflow.UtcNow);
                        ciWait = await WaitForCiSignalAsync(ciWait, rerun.SignalsBeforeRerun);
                        continue;
                    }

                    if (rerun.Stale)
                    {
                        ciWait = ciWait.Pending(Workflow.UtcNow);
                        if (!ciWait.TimedOut)
                        {
                            ciWait = await WaitForCiSignalAsync(ciWait, seen);
                            continue;
                        }

                        decision = TriageRules.CiTimeoutDecision();
                        break;
                    }

                    decision = assess.Decision;
                    break;
                case AssessStatus.Decided:
                    decision = assess.Decision;
                    break;
                default:
                    await ProgressAsync(input, assess.HeadSha, "Running the rules and Jev.");
                    stepStarted = Workflow.UtcNow;
                    var judge = await JudgeWithJevAsync(input, assess.HeadSha, body, null);
                    await RecordStepAsync(run, ++seq, "jev", LadderRung.Jev, JudgeStopReason(judge), stepStarted, judge);
                    if (judge.HeadMoved)
                    {
                        continue;
                    }

                    decision = judge.Decision;
                    if (_step2 && decision is { Reason: TriageRules.NoChangelog } && body is not null)
                    {
                        var settled = await RetryWithChangelogsAsync(input, run, seq, assess.HeadSha, body);
                        seq = settled.Seq;
                        if (settled.HeadMoved)
                        {
                            continue;
                        }

                        decision = settled.Decision ?? decision;
                    }

                    break;
            }

            if (_claude && (climbReason is not null || WantsAgent(assess, decision)))
            {
                var climbed = await ClimbAsync(input, run, seq, assess.HeadSha, climbReason ?? decision!.Reason, body);
                seq = _seq;
                if (climbed is null)
                {
                    continue;
                }

                return climbed;
            }

            var verdict = decision!;
            if (_step2 && assess.Mode == TriageMode.Live)
            {
                if (verdict.Verdict == TriageVerdict.Approve && precheckedSha != assess.HeadSha)
                {
                    var applyCheck = await PrecheckAsync(input, run, ++seq, assess.HeadSha, "apply-precheck");
                    if (applyCheck.HeadMoved)
                    {
                        continue;
                    }

                    if (applyCheck.Verdict != PrecheckVerdict.Passed)
                    {
                        verdict = TriageRules.PrecheckDecision(applyCheck.Verdict);
                    }
                }
            }

            if (_claude && assess.Mode == TriageMode.Live && !assess.Stored && verdict.Verdict == TriageVerdict.NeedsReview)
            {
                verdict = verdict with { Verdict = TriageVerdict.Owner };
            }

            return await RecordVerdictAsync(input, run, ++seq, assess.HeadSha, verdict, assess.Mode, body, null);
        }
    }

    private static async Task<PrecheckResult> PrecheckAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, string stepName)
    {
        var stepStarted = Workflow.UtcNow;
        var result = await Workflow.ExecuteActivityAsync(
            (RenovateTriageCodeActivities activities) => activities.PrecheckAsync(new PrecheckInput(input.Repo, input.PrNumber, headSha)),
            ScanOptions);
        await RecordStepAsync(
            run,
            seq,
            stepName,
            LadderRung.Rule,
            result.HeadMoved ? "head_moved" : JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Verdict.ToString()),
            stepStarted,
            new { result.HeadMoved, result.Verdict, result.Detail });
        return result;
    }

    private sealed record CiFailureStep(bool HeadMoved, bool Rerun, bool Stale, int Seq, int SignalsBeforeRerun);

    private async Task<CiFailureStep> ClassifyAndRerunAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, TriageMode mode, bool afterRerun)
    {
        var stepStarted = Workflow.UtcNow;
        var classified = await Workflow.ExecuteActivityAsync(
            (RenovateTriageCodeActivities activities) => activities.ClassifyCiAsync(new CiCodeInput(input.Repo, input.PrNumber, headSha, afterRerun)),
            _judgeOptions);
        var reason = classified switch
        {
            { HeadMoved: true } => "head_moved",
            { Stale: true } => "stale",
            { JevError: not null } => "jev_error",
            _ => JsonNamingPolicy.SnakeCaseLower.ConvertName(classified.Kind.ToString()),
        };
        await RecordStepAsync(run, seq, "ci-classify", LadderRung.Jev, reason, stepStarted, new { classified.HeadMoved, classified.Kind, classified.FailingJobs, classified.JevError });
        if (classified.HeadMoved)
        {
            return new CiFailureStep(true, false, false, seq, 0);
        }

        if (classified.Stale)
        {
            return new CiFailureStep(false, false, true, seq, 0);
        }

        if (classified.Kind != CiFailureKind.Flaky || mode != TriageMode.Live)
        {
            return new CiFailureStep(false, false, false, seq, 0);
        }

        var signalsBeforeRerun = _signalCount;
        stepStarted = Workflow.UtcNow;
        var rerun = await Workflow.ExecuteActivityAsync(
            (RenovateTriageCodeActivities activities) => activities.RerunFailedJobsAsync(new CiCodeInput(input.Repo, input.PrNumber, headSha)),
            _activityOptions);
        await RecordStepAsync(run, ++seq, "ci-rerun", LadderRung.Rule, rerun.HeadMoved ? "head_moved" : JsonNamingPolicy.SnakeCaseLower.ConvertName(rerun.Outcome.ToString()), stepStarted, rerun);
        return new CiFailureStep(rerun.HeadMoved, !rerun.HeadMoved && rerun.Outcome != RerunOutcome.AttemptLimit, false, seq, signalsBeforeRerun);
    }

    private async Task<CiWait> WaitForCiSignalAsync(CiWait wait, int signalsSeen)
    {
        var remaining = wait.Remaining(Workflow.UtcNow, CiTimeout);
        return remaining > TimeSpan.Zero && await Workflow.WaitConditionAsync(() => _signalCount > signalsSeen, remaining)
            ? wait
            : wait with { TimedOut = true };
    }

    private static async Task<(bool HeadMoved, TriageDecision? Decision, int Seq)> RetryWithChangelogsAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, ScannedText body)
    {
        var stepStarted = Workflow.UtcNow;
        var fetched = await Workflow.ExecuteActivityAsync(
            (RenovateTriageCodeActivities activities) => activities.FetchChangelogAsync(new FetchChangelogInput(input.Repo, input.PrNumber, headSha, body)),
            ScanOptions);
        var held = fetched.Sections.Count(s => s.Outcome != ScanOutcome.Clean);
        await RecordStepAsync(
            run,
            ++seq,
            "changelog",
            LadderRung.Rule,
            fetched.HeadMoved ? "head_moved" : fetched.Sections.Count == 0 ? "none" : "fetched",
            stepStarted,
            new { fetched.HeadMoved, sections = fetched.Sections.Count, held });
        if (fetched.HeadMoved)
        {
            return (true, null, seq);
        }

        if (held > 0 || fetched.Sections.Count == 0)
        {
            return (false, null, seq);
        }

        var clean = fetched.Sections.Select(s => s.Text).ToList();

        stepStarted = Workflow.UtcNow;
        var judge = await JudgeWithJevAsync(input, headSha, body, clean);
        await RecordStepAsync(run, ++seq, "jev-changelog", LadderRung.Jev, JudgeStopReason(judge), stepStarted, judge);
        return (judge.HeadMoved, judge.Jev is null ? null : judge.Decision, seq);
    }

    private async Task<int> BuildContextAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, string reason, ScannedText? body)
    {
        var stepStarted = Workflow.UtcNow;
        try
        {
            var built = await Workflow.ExecuteActivityAsync(
                (RenovateTriageCodeActivities activities) => activities.BuildContextAsync(new BuildContextInput(input.Repo, input.PrNumber, headSha, reason, body)),
                ScanOptions);
            _context = built.Context;
            await RecordStepAsync(run, ++seq, "context", LadderRung.Rule, built.HeadMoved ? "head_moved" : "built", stepStarted, new { built.HeadMoved, sections = built.Context?.Sections.Count });
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogContextFailed(Workflow.Logger, ex);
            await RecordStepAsync(run, ++seq, "context", LadderRung.Rule, "error", stepStarted, new { error = true });
        }

        return seq;
    }

    private static async Task<JudgeResult> JudgeWithJevAsync(RenovateTriageInput input, string headSha, ScannedText? body, IReadOnlyList<ScannedText>? changelogs)
    {
        if (!Workflow.Patched(JevRetryPatch))
        {
            return await Workflow.ExecuteActivityAsync(
                (RenovateTriageActivities activities) => activities.JudgeAsync(new JudgeInput(input.Repo, input.PrNumber, headSha)),
                _activityOptions);
        }

        try
        {
            return await Workflow.ExecuteActivityAsync(
                (RenovateTriageActivities activities) => activities.JudgeAsync(new JudgeInput(input.Repo, input.PrNumber, headSha, RetryJevErrors: true, Body: body, Changelogs: changelogs)),
                _judgeOptions);
        }
        catch (ActivityFailureException ex) when (ex.InnerException is ApplicationFailureException { ErrorType: RenovateTriageActivities.JevErrorFailureType } cause)
        {
            return new JudgeResult(false, TriageRules.JevErrorDecision(), JevError: cause.Message);
        }
    }

    private static string JudgeStopReason(JudgeResult judge) => judge switch
    {
        { HeadMoved: true } => "head_moved",
        { JevError: not null } => "error",
        { Jev: null } => "skipped",
        _ => "answered",
    };

    private async Task<RenovateTriageResult> RecordVerdictAsync(RenovateTriageInput input, BeginRunResult? run, int seq, string headSha, TriageDecision decision, TriageMode mode, ScannedText? body, LadderRung? rung)
    {
        var stepStarted = Workflow.UtcNow;
        var recorded = await Workflow.ExecuteActivityAsync(
            (RenovateTriageActivities activities) => activities.RecordVerdictAsync(new RecordVerdictInput(input.Repo, input.PrNumber, headSha, decision, mode, rung)),
            _activityOptions);
        await RecordStepAsync(run, seq, "verdict", rung ?? LadderRung.Rule, decision.Reason, stepStarted, new { decision, recorded });
        ApplyResult? applied = null;
        if (mode == TriageMode.Live && Workflow.Patched(LivePatch))
        {
            stepStarted = Workflow.UtcNow;
            applied = await Workflow.ExecuteActivityAsync(
                (RenovateTriageActivities activities) => activities.ApplyAsync(new ApplyInput(input.Repo, input.PrNumber, headSha, decision, mode)),
                _applyOptions);
            await RecordStepAsync(run, seq + 1, "apply", LadderRung.Rule, applied.Outcome.ToString(), stepStarted, applied);
        }

        var effective = applied?.Effective ?? decision;
        if (applied is { Outcome: ApplyOutcome.Applied })
        {
            await FinishCheckAsync(input, headSha, CheckRunConclusion.Success, $"Verdict applied: {effective.Reason}.");
        }
        else if (applied is not null)
        {
            await FinishCheckAsync(input, headSha, CheckRunConclusion.Neutral, $"Verdict {decision.Reason} was not applied: {applied.Reason}.");
        }
        else
        {
            await FinishCheckAsync(input, headSha, CheckRunConclusion.Neutral, $"Verdict {decision.Reason} was recorded without writes.");
        }

        if (_claude && applied is { Outcome: ApplyOutcome.Aborted, Reason: RenovateTriageActivities.HeadMovedReason } && Workflow.Patched(HeadPatch))
        {
            await CompleteAsync(run);
            throw Workflow.CreateContinueAsNewException((RenovateTriageWorkflow wf) => wf.RunAsync(input));
        }

        if (_step2 && !_claude && applied is { Outcome: ApplyOutcome.Applied } && TriageRules.Climbs(decision))
        {
            await BuildContextAsync(input, run, seq + 1, headSha, decision.Reason, body);
        }

        await CompleteAsync(run);

        return new RenovateTriageResult(RenovateTriageOutcome.Judged, headSha, decision.Reason, decision.Verdict);
    }

    private bool Reporting => _step2 && _mode == TriageMode.Live;

    private async Task ProgressAsync(RenovateTriageInput input, string headSha, string summary)
    {
        if (!Reporting || headSha.Length == 0 || (_checkSha == headSha && _checkSummary == summary))
        {
            return;
        }

        _checkSha = headSha;
        _checkSummary = summary;
        await ReportCheckAsync(input, headSha, CheckRunStatus.InProgress, null, summary);
    }

    private async Task FinishCheckAsync(RenovateTriageInput input, string headSha, CheckRunConclusion conclusion, string summary)
    {
        var target = Reporting ? headSha : _checkSha;
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        _checkSha = null;
        _checkSummary = null;
        await ReportCheckAsync(input, target, CheckRunStatus.Completed, conclusion, summary);
    }

    private async Task CloseCheckAsync(RenovateTriageInput input)
    {
        if (_checkSha is not { } headSha)
        {
            return;
        }

        _checkSha = null;
        var cancelled = Workflow.CancellationToken.IsCancellationRequested;
        await ReportCheckAsync(
            input,
            headSha,
            CheckRunStatus.Completed,
            cancelled ? CheckRunConclusion.Neutral : CheckRunConclusion.Failure,
            cancelled ? "Triage was cancelled." : "Triage stopped on an error.");
    }

    private static async Task ReportCheckAsync(RenovateTriageInput input, string headSha, CheckRunStatus status, CheckRunConclusion? conclusion, string summary)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(
                (TriageCheckActivities activities) => activities.ReportAsync(new TriageCheckInput(input.Repo, input.PrNumber, headSha, status, conclusion, summary)),
                new ActivityOptions
                {
                    StartToCloseTimeout = _activityOptions.StartToCloseTimeout,
                    ScheduleToCloseTimeout = _activityOptions.ScheduleToCloseTimeout,
                    RetryPolicy = _activityOptions.RetryPolicy,
                    CancellationToken = CancellationToken.None,
                });
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogCheckFailed(Workflow.Logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not report the Agent triage check run; the triage carries on without it.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the triage context; the triage carries on without it.")]
    private static partial void LogContextFailed(ILogger logger, Exception exception);

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
