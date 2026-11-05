using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Workflows;

public class AgentPodStepRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(RunAgentPodInput Input, ActivityInfo Info)> RunStepAsync(PodRunProbeInput probe)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var pods = new ScriptedPods();
        ActivityInfo? info = null;
        pods.OnRun = (_, activity) =>
        {
            info = activity;
            return Task.FromResult(ScriptedPods.Succeeded());
        };
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<PodRunProbeWorkflow>().AddAllActivities(new RecordingCleanup()));
        using var podWorker = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(pods));

        await workflows.ExecuteAsync(
            () => podWorker.ExecuteAsync(
                () => env.Client.ExecuteWorkflowAsync(
                    (PodRunProbeWorkflow wf) => wf.RunAsync(probe),
                    new WorkflowOptions($"probe-{Guid.NewGuid()}", TaskQueues.AgentPlatform)),
                Ct),
            Ct);

        return (Assert.Single(pods.Inputs), info!);
    }

    [Fact]
    public async Task ARunWithNoOverrides_KeepsTheStandardActivityTimeoutsAndSendsNoOverrides()
    {
        var (input, info) = await RunStepAsync(new PodRunProbeInput(null, null));

        Assert.Equal(AgentRunTimeouts.ActivityScheduleToClose, info.ScheduleToCloseTimeout);
        Assert.Equal(AgentRunTimeouts.ActivityStartToClose(AgentRunTimeouts.DefaultPodDeadlineSeconds), info.StartToCloseTimeout);
        Assert.Null(input.Overrides);
    }

    [Fact]
    public async Task ARunWithOverrides_SendsThemToTheActivityAndAppliesTheScheduleToCloseToTheActivityOptions()
    {
        var overrides = new AgentRunOverrides("opus", 90, 7.5m, new Dictionary<string, string> { ["contents"] = "read" });
        var scheduleToClose = TimeSpan.FromMinutes(57);

        var (input, info) = await RunStepAsync(new PodRunProbeInput(overrides, scheduleToClose));

        Assert.Equal(scheduleToClose, info.ScheduleToCloseTimeout);
        Assert.Equal(AgentRunTimeouts.ActivityStartToClose(AgentRunTimeouts.DefaultPodDeadlineSeconds), info.StartToCloseTimeout);
        Assert.Equal(("opus", 90, 7.5m), (input.Overrides!.Model, input.Overrides.MaxTurns, input.Overrides.MaxBudgetUsd));
        Assert.Equal(overrides.TokenPermissions, input.Overrides.TokenPermissions);
    }
}
