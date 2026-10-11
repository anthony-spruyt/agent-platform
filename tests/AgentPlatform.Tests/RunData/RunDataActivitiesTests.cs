using System.Text.Json;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    public async Task RecordStep_WithAnUnknownPromptHash_FailsWithoutRetry()
    {
        var data = InMemoryRunData.Create();
        var activities = data.Activities;
        var env = Environment();
        var run = await env.RunAsync(() => activities.BeginRunAsync(Begin()));

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => env.RunAsync(() => activities.RecordStepAsync(
                new RecordStepInput(run.RunId, 1, "ping", LadderRung.Rule, "deterministic", Started, 7, PromptHash: new string('0', 64)))));

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

    private static DbUpdateException DatabaseFailure(string sqlState) =>
        new("The database rejected the write.", new PostgresException("rejected", "ERROR", "ERROR", sqlState));

    [Theory]
    [InlineData("22021")]
    [InlineData("22001")]
    [InlineData("22P05")]
    [InlineData("23502")]
    [InlineData("23503")]
    [InlineData("23505")]
    [InlineData("23514")]
    public async Task BeginRun_WhenTheDatabaseRejectsTheDataPermanently_FailsWithoutRetry(string sqlState)
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(DatabaseFailure(sqlState));
        var activities = data.Activities;

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Environment().RunAsync(() => activities.BeginRunAsync(Begin())));

        Assert.True(failure.NonRetryable);
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("57P01")]
    [InlineData("08006")]
    [InlineData("53300")]
    public async Task BeginRun_WhenTheDatabaseFailsTransiently_StaysRetryable(string sqlState)
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(DatabaseFailure(sqlState));
        var activities = data.Activities;

        await Assert.ThrowsAsync<DbUpdateException>(() => Environment().RunAsync(() => activities.BeginRunAsync(Begin())));
    }

    [Fact]
    public async Task BeginRun_WhenTheWriteFailsWithoutAPostgresError_StaysRetryable()
    {
        var data = InMemoryRunData.Create();
        data.FailSavesWith(new DbUpdateException("lost connection"));
        var activities = data.Activities;

        await Assert.ThrowsAsync<DbUpdateException>(() => Environment().RunAsync(() => activities.BeginRunAsync(Begin())));
    }

    [Fact]
    public async Task RecordStepAndCompleteRun_WhenTheDatabaseRejectsTheDataPermanently_FailWithoutRetry()
    {
        var data = InMemoryRunData.Create();
        var failing = data.Activities;
        var run = await Environment().RunAsync(() => failing.BeginRunAsync(Begin()));
        data.FailSavesWith(DatabaseFailure("22021"));

        var step = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Environment().RunAsync(() => failing.RecordStepAsync(new RecordStepInput(run.RunId, 1, "ping", LadderRung.Rule, "deterministic", Started, 7))));
        var complete = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Environment().RunAsync(() => failing.CompleteRunAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Started.AddSeconds(1)))));

        Assert.True(step.NonRetryable);
        Assert.True(complete.NonRetryable);
    }
}
