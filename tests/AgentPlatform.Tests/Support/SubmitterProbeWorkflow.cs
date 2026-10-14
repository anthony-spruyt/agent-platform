using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.Workflows;
using Temporalio.Workflows;

namespace AgentPlatform.Tests.Support;

public sealed record ProbeResult(AgentPodStepStatus Status, string? Summary);

// Smallest workflow that answers the agent result update, so the submitter is tested without the triage workflow's other activities.
[Workflow]
public sealed class SubmitterProbeWorkflow
{
    public const string Step = "triage";

    private readonly AgentPodStep _podStep;

    private SubmitResultPayload? _result;

    public SubmitterProbeWorkflow() => _podStep = new AgentPodStep(Step, 1, payload =>
    {
        _result = payload;
        return null;
    });

    [WorkflowUpdate(AgentResultUpdates.SubmitResult)]
    public Task SubmitResultAsync(SubmitResultPayload payload) => _podStep.AcceptAsync(payload);

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload) => AgentPodStep.Validate(_podStep, payload);

    [WorkflowRun]
    public async Task<ProbeResult> RunAsync()
    {
        var outcome = await _podStep.RunAsync("p", AgentTier.Read, null, AgentRunTimeouts.DefaultPodDeadlineSeconds, Workflow.CancellationToken);
        return new ProbeResult(outcome.Status, _result?.Summary);
    }
}
