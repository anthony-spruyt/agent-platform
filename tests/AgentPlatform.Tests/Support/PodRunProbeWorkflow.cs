using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Temporalio.Workflows;

namespace AgentPlatform.Tests.Support;

public sealed record PodRunProbeInput(AgentRunOverrides? Overrides, TimeSpan? ScheduleToClose);

[Workflow]
public sealed class PodRunProbeWorkflow
{
    private readonly AgentPodStep _podStep = new("claude", 1, _ => null);

    [WorkflowRun]
    public async Task<AgentPodStepStatus> RunAsync(PodRunProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var outcome = await _podStep.RunAsync(
            "p",
            AgentTier.Read,
            new AgentRepoContext("octo/widgets", null, new string('a', 40)),
            AgentRunTimeouts.DefaultPodDeadlineSeconds,
            Workflow.CancellationToken,
            input.Overrides,
            input.ScheduleToClose);
        return outcome.Status;
    }
}
