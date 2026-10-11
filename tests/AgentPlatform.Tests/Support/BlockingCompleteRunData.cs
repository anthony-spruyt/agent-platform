using AgentPlatform.Host.RunData;
using Temporalio.Activities;

namespace AgentPlatform.Tests.Support;

internal sealed class BlockingCompleteRunData
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid _runId = Guid.NewGuid();
    private int _completeCalls;

    public Task Blocked => _blocked.Task;

    public int RecordStepCalls { get; private set; }

    [Activity(RunDataActivities.BeginRunName)]
    public BeginRunResult BeginRun(BeginRunInput input) => new(_runId, null);

    [Activity(RunDataActivities.RecordStepName)]
    public void RecordStep(RecordStepInput input) => RecordStepCalls++;

    [Activity(RunDataActivities.CompleteRunName)]
    public async Task CompleteRun(CompleteRunInput input)
    {
        if (Interlocked.Increment(ref _completeCalls) > 1)
        {
            return;
        }

        var context = ActivityExecutionContext.Current;
        _blocked.TrySetResult();
        while (true)
        {
            context.Heartbeat();
            await Task.Delay(50, context.CancellationToken);
        }
    }
}
