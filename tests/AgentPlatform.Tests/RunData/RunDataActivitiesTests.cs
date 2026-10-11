using System.Text.Json;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RunData;

public class RunDataActivitiesTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ActivityEnvironment Environment() =>
        new() { Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "wf-9", WorkflowRunId = "run-9" } };

    private static BeginRunInput Begin() =>
        new("PingWorkflow", JsonDocument.Parse("""{"name":"x"}""").RootElement.Clone(), Started);

    [Fact]
    public async Task BeginRun_RecordsTheWorkflowAndRunIdsOfTheCallingWorkflow()
    {
        var data = InMemoryRunData.Create();
        var activities = data.Activities;

        var result = await Environment().RunAsync(() => activities.BeginRunAsync(Begin()));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal("wf-9", run.WorkflowId);
        Assert.Equal("run-9", run.WorkflowRunId);
    }

    [Fact]
    public async Task BeginRun_WithoutAnInputEvent_FailsWithoutRetry()
    {
        var activities = InMemoryRunData.Create().Activities;

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Environment().RunAsync(() => activities.BeginRunAsync(Begin() with { InputEvent = default })));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task RecordStepAndCompleteRun_WriteThroughTheStore()
    {
        var data = InMemoryRunData.Create();
        var activities = data.Activities;
        var env = Environment();
        var run = await env.RunAsync(() => activities.BeginRunAsync(Begin()));

        await env.RunAsync(() => activities.RecordStepAsync(
            new RecordStepInput(run.RunId, 1, "ping", LadderRung.Rule, "deterministic", Started, 7, Confidence: 1)));
        await env.RunAsync(() => activities.CompleteRunAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Started.AddSeconds(1))));

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.RunSteps.CountAsync(Ct));
        Assert.Equal(RunStatus.Succeeded, (await db.Runs.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task RecordStep_WithInvalidConfidence_FailsWithoutRetry()
    {
        var data = InMemoryRunData.Create();
        var activities = data.Activities;
        var env = Environment();
        var run = await env.RunAsync(() => activities.BeginRunAsync(Begin()));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => activities.RecordStepAsync(
                new RecordStepInput(run.RunId, 1, "ping", LadderRung.Rule, "deterministic", Started, 7, Confidence: 2))));

        Assert.True(failure.NonRetryable);
    }
}
