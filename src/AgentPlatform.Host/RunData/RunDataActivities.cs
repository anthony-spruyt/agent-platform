using Microsoft.EntityFrameworkCore;
using Npgsql;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RunData;

public sealed class RunDataActivities(RunDataStore store)
{
    public const string BeginRunName = "BeginRun";
    public const string RecordStepName = "RecordStep";
    public const string CompleteRunName = "CompleteRun";

    [Activity(BeginRunName)]
    public Task<BeginRunResult> BeginRunAsync(BeginRunInput input)
    {
        var context = ActivityExecutionContext.Current;
        var workflowId = context.Info.WorkflowId ?? throw MustBeStartedByAWorkflow();
        var workflowRunId = context.Info.WorkflowRunId ?? throw MustBeStartedByAWorkflow();
        return GuardAsync(() => store.BeginRunAsync(workflowId, workflowRunId, input, context.CancellationToken));
    }

    [Activity(RecordStepName)]
    public Task RecordStepAsync(RecordStepInput input) =>
        GuardAsync(() => store.RecordStepAsync(input, ActivityExecutionContext.Current.CancellationToken));

    [Activity(CompleteRunName)]
    public Task CompleteRunAsync(CompleteRunInput input) =>
        GuardAsync(() => store.CompleteRunAsync(input, ActivityExecutionContext.Current.CancellationToken));

    private static ApplicationFailureException MustBeStartedByAWorkflow() =>
        new("Run data activities must be started by a workflow.", nonRetryable: true);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or UnknownRunException)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
        catch (DbUpdateException ex) when (IsPermanent(ex))
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
    }

    // SQLSTATE class 22 (data exception) and 23 (integrity constraint violation): the same input fails every time.
    private static bool IsPermanent(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: { } state } && (state.StartsWith("22", StringComparison.Ordinal) || state.StartsWith("23", StringComparison.Ordinal));

    private static async Task GuardAsync(Func<Task> action) =>
        await GuardAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
}
