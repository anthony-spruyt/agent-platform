using System.Text.Json;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests;

public class PingWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TemporalWorker NewWorker<T>(WorkflowEnvironment env, InMemoryRunData data, T pingActivities)
        where T : class =>
        NewWorker(env, data.Activities, pingActivities);

    private static TemporalWorker NewWorker<TRunData, TPing>(WorkflowEnvironment env, TRunData runData, TPing pingActivities)
        where TRunData : class
        where TPing : class =>
        new(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<PingWorkflow>()
                .AddAllActivities(pingActivities)
                .AddAllActivities(runData));

    [Fact]
    public async Task RunAsync_ReturnsPongFromActivity()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = NewWorker(env, InMemoryRunData.Create(), new PingActivities());

        var result = await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                var output = await handle.GetResultAsync();
                await HistoryStore.RecordAsync(handle, "ping-happy-path");
                return output;
            },
            Ct);

        Assert.Equal("pong: platform", result);
    }

    [Fact]
    public async Task RunAsync_RecordsASucceededRunWithItsRuleStep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();
        using var worker = NewWorker(env, data, new PingActivities());
        var workflowId = $"ping-{Guid.NewGuid()}";

        await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions(workflowId, TaskQueues.AgentPlatform));
                return await handle.GetResultAsync();
            },
            Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        Assert.Equal(workflowId, run.WorkflowId);
        Assert.Equal("PingWorkflow", run.WorkflowType);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal("platform", JsonDocument.Parse(run.InputEvent).RootElement.GetProperty("name").GetString());
        var step = await db.RunSteps.SingleAsync(Ct);
        Assert.Equal(run.Id, step.RunId);
        Assert.Equal(LadderRung.Rule, step.Rung);
        Assert.Equal(1.0, step.Confidence);
        Assert.Equal("pong: platform", JsonDocument.Parse(step.Output!).RootElement.GetProperty("result").GetString());
    }

    private sealed class FailingPing
    {
        public int Calls { get; private set; }

        [Activity("Ping")]
        public string Ping(string name)
        {
            Calls++;
            throw new ApplicationFailureException("boom", nonRetryable: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenThePingFails_RecordsAFailedRunAndStillFails()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();
        using var worker = NewWorker(env, data, new FailingPing());

        await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
                return 0;
            },
            Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        Assert.Empty(db.RunSteps);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_WhenRecordingTheRunFails_StillReturnsPong(bool beginSucceeds, bool retryable)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var runData = new FailingRunData(beginSucceeds, retryable);
        var ping = new CountingPing();
        using var worker = NewWorker(env, runData, ping);

        var result = await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                return await handle.GetResultAsync();
            },
            Ct);

        Assert.Equal("pong: platform", result);
        Assert.Equal(1, ping.Calls);
        Assert.True(runData.BeginCalls >= 1);
        if (beginSucceeds)
        {
            Assert.True(runData.RecordStepCalls >= 1);
            Assert.True(runData.CompleteCalls >= 1);
        }
        else
        {
            Assert.Equal(0, runData.RecordStepCalls);
            Assert.Equal(0, runData.CompleteCalls);
        }
    }

    [Fact]
    public async Task RunAsync_WhenThePingFailsAndRecordingFails_StillFailsWithThePingFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = NewWorker(env, new FailingRunData(true, false), new FailingPing());

        await worker.ExecuteAsync(
            async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (PingWorkflow wf) => wf.RunAsync("platform"),
                    new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));
                var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
                Assert.Equal("boom", Assert.IsType<ActivityFailureException>(failure.InnerException).InnerException!.Message);
                return 0;
            },
            Ct);
    }

    private sealed class CountingPing
    {
        public int Calls { get; private set; }

        [Activity("Ping")]
        public string Ping(string name)
        {
            Calls++;
            return $"pong: {name}";
        }
    }
}
