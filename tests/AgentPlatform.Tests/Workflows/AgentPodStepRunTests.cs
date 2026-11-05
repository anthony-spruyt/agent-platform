using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Workflows;

public class AgentPodStepRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<AgentPodStepStatus> ExecuteProbeAsync(PodRunProbeInput probe, ScriptedPods pods, CancellationToken cancellationToken)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<PodRunProbeWorkflow>().AddAllActivities(new RecordingCleanup()));
        using var podWorker = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(pods));

        return await workflows.ExecuteAsync(
            () => podWorker.ExecuteAsync(
                () => env.Client.ExecuteWorkflowAsync(
                    (PodRunProbeWorkflow wf) => wf.RunAsync(probe),
                    new WorkflowOptions($"probe-{Guid.NewGuid()}", TaskQueues.AgentPlatform)),
                cancellationToken),
            cancellationToken);
    }

    private static async Task<(RunAgentPodInput Input, ActivityInfo Info)> RunStepAsync(PodRunProbeInput probe)
    {
        var pods = new ScriptedPods();
        ActivityInfo? info = null;
        pods.OnRun = (_, activity) =>
        {
            info = activity;
            return Task.FromResult(ScriptedPods.Succeeded());
        };

        await ExecuteProbeAsync(probe, pods, Ct);

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AScheduleToCloseOfZeroOrLess_FailsTheWorkflowWithoutSchedulingAnyActivity(int seconds)
    {
        var pods = new ScriptedPods();
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cap.CancelAfter(TimeSpan.FromSeconds(60));

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => ExecuteProbeAsync(new PodRunProbeInput(null, TimeSpan.FromSeconds(seconds)), pods, cap.Token));

        var cause = Assert.IsType<ApplicationFailureException>(failure.InnerException);
        Assert.True(cause.NonRetryable);
        Assert.Empty(pods.Inputs);
    }
}
