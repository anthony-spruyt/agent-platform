using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

[Workflow]
public class RenovateTriageWorkflow
{
    public const string CheckSuiteCompletedSignalName = "CheckSuiteCompleted";

    private readonly List<CheckSuiteCompletedSignal> _signals = [];

    public static string WorkflowId(string repo, int prNumber) => $"renovate-triage/{repo}/{prNumber}";

    [WorkflowSignal(CheckSuiteCompletedSignalName)]
    public Task OnCheckSuiteCompletedAsync(CheckSuiteCompletedSignal signal)
    {
        _signals.Add(signal);
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<IReadOnlyList<CheckSuiteCompletedSignal>> RunAsync()
    {
        await Workflow.WaitConditionAsync(() => _signals.Count > 0);
        return _signals;
    }
}
