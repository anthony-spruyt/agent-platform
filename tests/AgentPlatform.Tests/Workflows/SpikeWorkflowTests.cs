using System.Net;
using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
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

    private static readonly SubmitResultPayload Second = Payload with { Summary = "second" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedActivities
    {
        public List<RunAgentPodInput> Inputs { get; } = [];

        public List<ActivityInfo> Infos { get; } = [];

        public Func<RunAgentPodInput, Task<AgentRunResult>> OnRun { get; set; } = _ => Task.FromResult(Succeeded());

        [Activity("RunAgentPod")]
        public Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
        {
            Inputs.Add(input);
            Infos.Add(ActivityExecutionContext.Current.Info);
            return OnRun(input);
        }
    }

    private sealed class RecordingCleanup
    {
        public List<string> Cleanups { get; } = [];

        [Activity("DeleteAgentPod")]
        public Task DeleteAgentPodAsync(string step)
        {
            Cleanups.Add(step);
            return Task.CompletedTask;
        }
    }

    private static AgentRunResult Succeeded() =>
        new(AgentRunOutcome.Succeeded, "pod-1", 0, "all good", null, new AgentRunUsage(1, 2, 0.1, 3, 4, "s"));

    private static async Task<T> InWorkersAsync<T, TPod, TCleanup>(
        WorkflowEnvironment env,
        TPod podActivities,
        TCleanup cleanup,
        Func<Task<T>> body)
        where TPod : class
        where TCleanup : class
    {
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<SpikeWorkflow>().AddAllActivities(cleanup));
        using var pods = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPods).AddAllActivities(podActivities));
        return await workflows.ExecuteAsync(() => pods.ExecuteAsync(body, Ct), Ct);
    }

    private static Task<WorkflowHandle<SpikeWorkflow, SpikeResult>> StartAsync(WorkflowEnvironment env, SpikeInput? input = null) =>
        env.Client.StartWorkflowAsync(
            (SpikeWorkflow wf) => wf.RunAsync(input ?? new SpikeInput("Say hello", AgentTier.Read)),
            new WorkflowOptions($"spike-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

    [Fact]
    public async Task HappyPath_ReturnsTheSubmittedResultWithRunMetadata()
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

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await running.Task;
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
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
    public async Task HappyPath_RunsThePodActivityOnTheAgentPodsQueueWithTheDeadlineAndHeartbeatTimeout()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, new SpikeInput("check the repo", AgentTier.Read));
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        var input = Assert.Single(activities.Inputs);
        Assert.Equal("check the repo", input.Prompt);
        Assert.Equal(AgentTier.Read, input.Tier);
        Assert.Equal(AgentRunTimeouts.DefaultPodDeadlineSeconds, input.DeadlineSeconds);
        var info = Assert.Single(activities.Infos);
        Assert.Equal(TaskQueues.AgentPods, info.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(60), info.HeartbeatTimeout);
        Assert.Equal(AgentRunTimeouts.ActivityStartToClose(input.DeadlineSeconds), info.StartToCloseTimeout);
    }

    [Fact]
    public async Task ThePodActivity_IsBoundedBelowThePodCleanupWindowAcrossAllRetries()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        var info = Assert.Single(activities.Infos);
        Assert.Equal(AgentRunTimeouts.ActivityScheduleToClose, info.ScheduleToCloseTimeout);
        Assert.True(AgentRunTimeouts.ActivityScheduleToClose < TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ASecondSubmit_IsRejectedAndTheFirstResultIsKept()
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

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await running.Task;
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            var rejected = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                () => handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Second)));
            Assert.Equal(SpikeWorkflow.ResultAlreadySubmittedErrorType, Assert.IsType<ApplicationFailureException>(rejected.InnerException).ErrorType);
            release.SetResult();
            return await handle.GetResultAsync();
        });

        Assert.Equal("Looks fine", result.Result!.Summary);
    }

    [Fact]
    public async Task SubmitArrivingAfterThePodEnds_IsStillAccepted()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => activities.Inputs.Count == 1);
            await Task.Delay(200, Ct);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task WhenNoResultArrives_TimesOutWithTheRunMetadata()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        var result = await InWorkersAsync(env, new ScriptedActivities(), new RecordingCleanup(), async () =>
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
    public async Task WhenTheAgentRunFails_ReportsAgentFailedWithoutWaitingForAResult(AgentRunOutcome outcome)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities
        {
            OnRun = _ => Task.FromResult(new AgentRunResult(outcome, "pod-1", 1, null, "why", null)),
        };

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
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
        var cleanup = new RecordingCleanup();
        var activities = new ScriptedActivities
        {
            OnRun = _ => throw new ApplicationFailureException("boom", nonRetryable: true),
        };

        await InWorkersAsync(env, activities, cleanup, async () =>
        {
            var handle = await StartAsync(env);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        });

        Assert.Equal(["spike"], cleanup.Cleanups);
    }

    [Fact]
    public async Task WhenTheRunEnds_ThePodIsDeletedOnceByTheWorkflowWorkerWithoutTheAgentPodsQueue()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var cleanup = new RecordingCleanup();

        await InWorkersAsync(env, new ScriptedActivities(), cleanup, async () =>
        {
            var handle = await StartAsync(env);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(["spike"], cleanup.Cleanups);
    }

    [Fact]
    public async Task WhenTheWorkflowIsCancelled_ItEndsCanceledAndDeletesThePodOnce()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var cleanup = new RecordingCleanup();
        var activities = new ScriptedActivities
        {
            OnRun = async _ =>
            {
                var context = ActivityExecutionContext.Current;
                while (true)
                {
                    context.Heartbeat();
                    await Task.Delay(50, context.CancellationToken);
                }
            },
        };

        var status = await InWorkersAsync(env, activities, cleanup, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => activities.Inputs.Count == 1);
            await handle.CancelAsync();
            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            Assert.IsType<CanceledFailureException>(failure.InnerException);
            return (await handle.DescribeAsync()).Status;
        });

        Assert.Equal(WorkflowExecutionStatus.Canceled, status);
        Assert.Equal(["spike"], cleanup.Cleanups);
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

        var result = await InWorkersAsync(env, activities, activities, async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => api.PodCreateAttempts >= 2);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
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
