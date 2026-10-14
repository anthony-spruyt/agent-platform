using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class ClaudeBudgetActivities(ClaudeBudgetStore store)
{
    public const string ReserveName = "ReserveClaudeRun";
    public const string MarkPodCreatedName = "MarkClaudePodCreated";
    public const string FinishName = "FinishClaudeRun";
    public const string SetCooldownName = "SetClaudeCooldown";

    [Activity(ReserveName)]
    public Task<ClaudeReservation> ReserveAsync(ReserveClaudeRunInput input)
    {
        var context = ActivityExecutionContext.Current;
        var workflowId = context.Info.WorkflowId ?? throw MustBeStartedByAWorkflow();
        return GuardAsync(() => store.ReserveAsync(workflowId, input, context.CancellationToken));
    }

    [Activity(MarkPodCreatedName)]
    public Task MarkPodCreatedAsync(Guid runId) =>
        GuardAsync(() => store.MarkPodCreatedAsync(runId, ActivityExecutionContext.Current.CancellationToken));

    [Activity(FinishName)]
    public Task FinishAsync(FinishClaudeRunInput input) =>
        GuardAsync(() => store.FinishAsync(input, ActivityExecutionContext.Current.CancellationToken));

    [Activity(SetCooldownName)]
    public Task<DateTimeOffset> SetCooldownAsync(SetClaudeCooldownInput input) =>
        GuardAsync(() => store.SetCooldownAsync(input, ActivityExecutionContext.Current.CancellationToken));

    private static ApplicationFailureException MustBeStartedByAWorkflow() =>
        new("Claude budget activities must be started by a workflow.", nonRetryable: true);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or UnknownClaudeRunException)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }
    }

    private static async Task GuardAsync(Func<Task> action) =>
        await GuardAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
}
