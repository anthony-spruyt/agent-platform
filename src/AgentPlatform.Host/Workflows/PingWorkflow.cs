using AgentPlatform.Host.RunData;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public class PingWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string name)
    {
        var started = Workflow.UtcNow;
        var run = await RunRecorder.BeginAsync(new BeginRunInput(nameof(PingWorkflow), RunRecorder.ToJson(new { name }), started));
        try
        {
            var result = await Workflow.ExecuteActivityAsync(
                (PingActivities activities) => activities.Ping(name),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

            if (run is not null)
            {
                await RunRecorder.RecordStepAsync(new RecordStepInput(
                    run.RunId,
                    1,
                    "ping",
                    LadderRung.Rule,
                    "deterministic",
                    started,
                    RunRecorder.ElapsedMs(started),
                    Input: RunRecorder.ToJson(new { name }),
                    Output: RunRecorder.ToJson(new { result }),
                    Confidence: 1));
                await RunRecorder.CompleteAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Workflow.UtcNow));
            }

            return result;
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
}
