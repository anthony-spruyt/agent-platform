using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public class RenovateTriageSweeperWorkflow
{
    public const string WorkflowId = "renovate-triage-sweeper-run";

    private static readonly ActivityOptions _activityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 3,
        },
    };

    [WorkflowRun]
    public async Task RunAsync()
    {
        var run = await RunRecorder.BeginAsync(new BeginRunInput(
            nameof(RenovateTriageSweeperWorkflow),
            RunRecorder.ToJson(new { trigger = "schedule" }),
            Workflow.UtcNow));

        var failed = false;
        failed |= await StepAsync(run, 1, "redrive", () => Workflow.ExecuteActivityAsync((SweeperActivities a) => a.RedriveAsync(), _activityOptions), r => r.Errors > 0);
        failed |= await StepAsync(run, 2, "redeliver", () => Workflow.ExecuteActivityAsync((SweeperActivities a) => a.RedeliverAsync(), _activityOptions), _ => false);
        failed |= await StepAsync(run, 3, "reconcile", () => Workflow.ExecuteActivityAsync((SweeperActivities a) => a.ReconcileAsync(), _activityOptions), r => r.Errors > 0);

        if (failed)
        {
            if (run is not null)
            {
                await RunRecorder.CompleteAfterFailureAsync(run.RunId);
            }

            return;
        }

        await Workflow.ExecuteActivityAsync((SweeperActivities a) => a.RecordSuccessAsync(), _activityOptions);
        if (run is not null)
        {
            await RunRecorder.CompleteAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Workflow.UtcNow));
        }
    }

    // One failing step must not stop the others: each recovers a different way a webhook can be lost.
    private static async Task<bool> StepAsync<T>(BeginRunResult? run, int seq, string name, Func<Task<T>> step, Func<T, bool> hadErrors)
    {
        var started = Workflow.UtcNow;
        object output;
        string stopReason;
        try
        {
            var result = await step();
            output = result!;
            stopReason = hadErrors(result) ? "error" : "completed";
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            output = new { error = ex.InnerException?.Message ?? ex.Message };
            stopReason = "error";
        }

        if (run is not null)
        {
            await RunRecorder.RecordStepAsync(new RecordStepInput(
                run.RunId,
                seq,
                name,
                LadderRung.Rule,
                stopReason,
                started,
                RunRecorder.ElapsedMs(started),
                Output: RunRecorder.ToJson(output)));
        }

        return stopReason == "error";
    }
}
