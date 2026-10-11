using System.Text.Json;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

internal static partial class RunRecorder
{
    private static readonly ActivityOptions _options = new()
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

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, _jsonOptions);

    public static async Task<BeginRunResult?> BeginAsync(BeginRunInput input)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync((RunDataActivities activities) => activities.BeginRunAsync(input), _options);
        }
        catch (ActivityFailureException ex)
        {
            LogRecordingFailed(Workflow.Logger, ex, RunDataActivities.BeginRunName);
            return null;
        }
    }

    public static async Task RecordStepAsync(RecordStepInput input)
    {
        try
        {
            await Workflow.ExecuteActivityAsync((RunDataActivities activities) => activities.RecordStepAsync(input), _options);
        }
        catch (ActivityFailureException ex)
        {
            LogRecordingFailed(Workflow.Logger, ex, RunDataActivities.RecordStepName);
        }
    }

    public static async Task CompleteAsync(CompleteRunInput input)
    {
        try
        {
            await Workflow.ExecuteActivityAsync((RunDataActivities activities) => activities.CompleteRunAsync(input), _options);
        }
        catch (ActivityFailureException ex)
        {
            LogRecordingFailed(Workflow.Logger, ex, RunDataActivities.CompleteRunName);
        }
    }

    public static async Task CompleteAfterFailureAsync(Guid runId, string? model = null)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(
                (RunDataActivities activities) => activities.CompleteRunAsync(new CompleteRunInput(runId, RunStatus.Failed, Workflow.UtcNow, model)),
                new ActivityOptions
                {
                    StartToCloseTimeout = _options.StartToCloseTimeout,
                    ScheduleToCloseTimeout = _options.ScheduleToCloseTimeout,
                    RetryPolicy = _options.RetryPolicy,
                    CancellationToken = CancellationToken.None,
                });
        }
        catch (ActivityFailureException ex)
        {
            LogRecordingFailed(Workflow.Logger, ex, RunDataActivities.CompleteRunName);
        }
    }

    public static long ElapsedMs(DateTime since) => Math.Max(0, (long)(Workflow.UtcNow - since).TotalMilliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write run data ({Activity}); the workflow carries on without it.")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string activity);
}
