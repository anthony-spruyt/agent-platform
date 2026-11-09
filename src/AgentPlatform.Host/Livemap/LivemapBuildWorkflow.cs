using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Livemap;

public sealed record LivemapBuildInput(long BuildId, string ManifestGid);

public sealed record LivemapBuildWake(string Sha, long SuiteId);

[Workflow]
public partial class LivemapBuildWorkflow
{
    public const string WakeSignalName = "livemap-build-wake";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(60);

    private static readonly RetryPolicy _retries = new()
    {
        InitialInterval = TimeSpan.FromSeconds(2),
        BackoffCoefficient = 2,
        MaximumInterval = TimeSpan.FromMinutes(1),
        MaximumAttempts = 3,
    };

    private static readonly ActivityOptions _readOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(10),
        RetryPolicy = _retries,
    };

    private static readonly ActivityOptions _classifyOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = _retries,
    };

    // A dispatch starts real work on the fork and is not safe to repeat, so a failure parks the build instead of retrying.
    private static readonly ActivityOptions _dispatchOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    private readonly HashSet<long> _wakes = [];

    private string _step = "start";

    public static string WorkflowId(long buildId) => $"livemap-build-{buildId}";

    [WorkflowSignal(WakeSignalName)]
    public Task WakeAsync(LivemapBuildWake wake)
    {
        LogReceived(Workflow.Logger, wake.SuiteId, wake.Sha);
        _wakes.Add(wake.SuiteId);
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<LivemapBuildOutcome> RunAsync(LivemapBuildInput input)
    {
        try
        {
            return await DriveAsync(input);
        }
        catch (ActivityFailureException)
        {
            return await ParkAsync(input.BuildId, $"The step '{_step}' failed after its retries.");
        }
    }

    private async Task<LivemapBuildOutcome> DriveAsync(LivemapBuildInput input)
    {
        if (!await IsInFlightAsync(input.BuildId))
        {
            return LivemapBuildOutcome.Superseded;
        }

        var (_, capture) = await DispatchAndWaitAsync(LivemapWorkflowFile.ExeCapture, "exe capture", input);
        if (capture is not { Succeeded: true })
        {
            return await ParkAsync(input.BuildId, Describe("The exe capture", capture));
        }

        if (!await IsInFlightAsync(input.BuildId))
        {
            return LivemapBuildOutcome.Superseded;
        }

        var (checkId, _) = await DispatchAndWaitAsync(LivemapWorkflowFile.Compat, "compat check", input);
        _step = "classify";
        return await Workflow.ExecuteActivityAsync(
            (LivemapBuildActivities activities) => activities.ClassifyAsync(new LivemapClassifyInput(input.BuildId, input.ManifestGid, checkId)),
            _classifyOptions);
    }

    private async Task<(long RunId, LivemapRun? Run)> DispatchAndWaitAsync(LivemapWorkflowFile file, string step, LivemapBuildInput input)
    {
        _step = $"{step} dispatch";
        var runId = await Workflow.ExecuteActivityAsync(
            (LivemapBuildActivities activities) => activities.DispatchAsync(new LivemapDispatchInput(file, input.BuildId, input.ManifestGid)),
            _dispatchOptions);
        _step = $"{step} wait";
        var deadline = Workflow.UtcNow + RunLimit;
        while (true)
        {
            var run = await Workflow.ExecuteActivityAsync((LivemapBuildActivities activities) => activities.GetRunAsync(runId), _readOptions);
            if (run is null || run.IsCompleted || Workflow.UtcNow >= deadline)
            {
                return (runId, run);
            }

            var suiteId = run.CheckSuiteId;
            await Workflow.WaitConditionAsync(() => suiteId > 0 && _wakes.Contains(suiteId), CheckInterval);
            TakeWakes(suiteId);
        }
    }

    private void TakeWakes(long runSuiteId)
    {
        foreach (var suiteId in _wakes)
        {
            if (suiteId != runSuiteId)
            {
                LogIgnored(Workflow.Logger, suiteId, runSuiteId);
            }
        }

        _wakes.Clear();
    }

    private static string Describe(string what, LivemapRun? run) => run switch
    {
        null => $"{what} could not be found.",
        { IsCompleted: false } => $"{what} did not finish in time.",
        _ => $"{what} ended with {run.Conclusion}.",
    };

    private static Task<bool> IsInFlightAsync(long buildId) =>
        Workflow.ExecuteActivityAsync((LivemapBuildActivities activities) => activities.IsInFlightAsync(buildId), _readOptions);

    private static Task<LivemapBuildOutcome> ParkAsync(long buildId, string detail) =>
        Workflow.ExecuteActivityAsync((LivemapBuildActivities activities) => activities.ParkAsync(new LivemapParkInput(buildId, detail)), _readOptions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received the wake of check suite {SuiteId} at {Sha}.")]
    private static partial void LogReceived(ILogger logger, long suiteId, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignored the wake of check suite {SuiteId}: the run being waited on belongs to check suite {RunSuiteId}.")]
    private static partial void LogIgnored(ILogger logger, long suiteId, long runSuiteId);
}
