using System.Net;
using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Workflows;

public class SpikeWorkflowTests
{
    private static readonly SubmitResultPayload Payload =
        new("success", "Looks fine", JsonDocument.Parse("""{"verdict":"safe"}""").RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedActivities
    {
        public List<RunAgentPodInput> Inputs { get; } = [];

        public Func<RunAgentPodInput, Task<AgentRunResult>> OnRun { get; set; } = _ => Task.FromResult(Succeeded());

        public List<string> Cleanups { get; } = [];

        [Activity("DeleteAgentPod")]
        public Task DeleteAgentPodAsync(string step)
        {
            Cleanups.Add(step);
            return Task.CompletedTask;
        }

        [Activity("RunAgentPod")]
        public Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
        {
            Inputs.Add(input);
            return OnRun(input);
        }
    }

    private static AgentRunResult Succeeded() =>
        new(AgentRunOutcome.Succeeded, "pod-1", 0, "all good", null, new AgentRunUsage(1, 2, 0.1, 3, 4, "s"));

    private static async Task<T> InWorkersAsync<T, TActivities>(
        WorkflowEnvironment env,
        TActivities activities,
        Func<Task<T>> body)
        where TActivities : class
    {
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<SpikeWorkflow>());
        using var pods = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(activities));
        return await workflows.ExecuteAsync(() => pods.ExecuteAsync(body, Ct), Ct);
    }

    private static Task<WorkflowHandle<SpikeWorkflow, SpikeResult>> StartAsync(WorkflowEnvironment env, SpikeInput? input = null) =>
        env.Client.StartWorkflowAsync(
            (SpikeWorkflow wf) => wf.RunAsync(input ?? new SpikeInput("Say hello", AgentTier.Read)),
            new WorkflowOptions($"spike-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

    [Fact]
    public async Task HappyPath_ReturnsTheSignalledResultWithRunMetadata()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var activities = new ScriptedActivities
        {
            OnRun = async _ =>
            {
                running.SetResult();
                await release.Task;
                return Succeeded();
            },
        };

        var result = await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await running.Task;
            await handle.SignalAsync(wf => wf.SubmitResultAsync(Payload));
            release.SetResult();
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal("Looks fine", result.Result!.Summary);
        Assert.Equal("safe", result.Result.Data!.Value.GetProperty("verdict").GetString());
        Assert.Equal("pod-1", result.Agent.PodName);
        Assert.Equal(AgentRunOutcome.Succeeded, result.Agent.Outcome);
        Assert.False(string.IsNullOrEmpty(result.WorkflowId));
        Assert.False(string.IsNullOrEmpty(result.RunId));
        Assert.Equal(AgentTier.Read, result.Tier);
    }

    [Fact]
    public async Task HappyPath_RunsThePodActivityOnTheAgentPodsQueueWithADeadlineAndHeartbeat()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        var handleId = await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env, new SpikeInput("check the repo", AgentTier.Read));
            await handle.SignalAsync(wf => wf.SubmitResultAsync(Payload));
            await handle.GetResultAsync();
            return handle.Id;
        });

        var input = Assert.Single(activities.Inputs);
        Assert.Equal("check the repo", input.Prompt);
        Assert.Equal(AgentTier.Read, input.Tier);
        Assert.InRange(input.DeadlineSeconds, 1, 3600);
        Assert.False(string.IsNullOrEmpty(handleId));
    }

    [Fact]
    public async Task SignalArrivingAfterThePodEnds_IsStillAccepted()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        var result = await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => activities.Inputs.Count == 1);
            await Task.Delay(200, Ct);
            await handle.SignalAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task WhenNoSignalArrives_TimesOutWithTheRunMetadata()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        var result = await InWorkersAsync(env, new ScriptedActivities(), async () =>
        {
            var handle = await StartAsync(env);
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.ResultTimeout, result.Status);
        Assert.Null(result.Result);
        Assert.Equal("pod-1", result.Agent.PodName);
    }

    [Theory]
    [InlineData(AgentRunOutcome.Truncated)]
    [InlineData(AgentRunOutcome.UsageLimit)]
    [InlineData(AgentRunOutcome.PodFailed)]
    [InlineData(AgentRunOutcome.TimedOut)]
    public async Task WhenTheAgentRunFails_ReportsAgentFailedWithoutWaitingForASignal(AgentRunOutcome outcome)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities
        {
            OnRun = _ => Task.FromResult(new AgentRunResult(outcome, "pod-1", 1, null, "why", null)),
        };

        var result = await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.AgentFailed, result.Status);
        Assert.Equal(outcome, result.Agent.Outcome);
        Assert.Equal("why", result.Agent.Detail);
    }

    [Fact]
    public async Task WhenEveryActivityAttemptFails_ThePodIsDeletedAndTheWorkflowFails()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities
        {
            OnRun = _ => throw new ApplicationFailureException("boom", nonRetryable: true),
        };

        await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        });

        Assert.Equal(["spike"], activities.Cleanups);
    }

    [Fact]
    public async Task WhenTheRunEnds_ThePodIsDeletedOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await handle.SignalAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(["spike"], activities.Cleanups);
    }

    [Fact]
    public async Task AFailedAttempt_IsRetriedAndReattachesToTheSamePodWithoutADuplicate()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var api = new FakeKubeApi();
        var failedOnce = false;
        api.Interceptor = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/log", StringComparison.Ordinal) && !failedOnce)
            {
                failedOnce = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }

            return null;
        };
        var options = new AgentPodsOptions();
        var runner = new KubernetesAgentRunner(
            api.CreateClient(),
            new AgentPodSpecBuilder(options),
            new AgentSessionTokens("0123456789abcdef0123456789abcdef"),
            [],
            options,
            TimeProvider.System,
            NullLogger<KubernetesAgentRunner>.Instance);
        var activities = new AgentPodActivities(runner, options, NullLogger<AgentPodActivities>.Instance);

        var result = await InWorkersAsync(env, activities, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => api.PodCreateAttempts >= 2);
            await handle.SignalAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal(2, api.PodCreateAttempts);
        Assert.Equal(1, api.PodsCreated);
        Assert.Empty(api.Pods);
    }

    [Fact]
    public void Input_AcceptsTheJsonTheTemporalCliSends()
    {
        var json = DataConverter.Default.PayloadConverter.ToPayload(JsonDocument.Parse("""{"prompt":"hello","tier":"read"}""").RootElement);

        var input = DataConverter.Default.PayloadConverter.ToValue<SpikeInput>(json);

        Assert.Equal(new SpikeInput("hello", AgentTier.Read), input);
    }

    [Fact]
    public void Input_AcceptsTheClusterReadTier()
    {
        var json = DataConverter.Default.PayloadConverter.ToPayload(JsonDocument.Parse("""{"prompt":"p","tier":"cluster-read"}""").RootElement);

        Assert.Equal(AgentTier.ClusterRead, DataConverter.Default.PayloadConverter.ToValue<SpikeInput>(json).Tier);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(20, Ct);
        }
    }
}
