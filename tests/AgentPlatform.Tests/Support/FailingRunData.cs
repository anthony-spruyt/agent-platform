using AgentPlatform.Host.RunData;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Tests.Support;

internal sealed class FailingRunData(bool beginSucceeds, bool retryable)
{
    public int BeginCalls { get; private set; }

    public int RecordStepCalls { get; private set; }

    public int CompleteCalls { get; private set; }

    [Activity(RunDataActivities.BeginRunName)]
    public BeginRunResult BeginRun(BeginRunInput input)
    {
        BeginCalls++;
        return beginSucceeds ? new BeginRunResult(Guid.NewGuid(), null) : throw Failure();
    }

    [Activity(RunDataActivities.RecordStepName)]
    public void RecordStep(RecordStepInput input)
    {
        RecordStepCalls++;
        throw Failure();
    }

    [Activity(RunDataActivities.CompleteRunName)]
    public void CompleteRun(CompleteRunInput input)
    {
        CompleteCalls++;
        throw Failure();
    }

    private ApplicationFailureException Failure() => new("recording failed", nonRetryable: !retryable);
}
