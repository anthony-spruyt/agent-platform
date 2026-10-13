using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Workflows;

public class AgentPodActivitiesTests
{
    private static readonly RunAgentPodInput Input = new("claude", "Say hello", AgentTier.Read, 1800);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRunner : IAgentRunner
    {
        public List<AgentRunRequest> Runs { get; } = [];

        public List<AgentRunRequest> Cancels { get; } = [];

        public Func<AgentRunRequest, CancellationToken, Task<AgentRunResult>> OnRun { get; set; } =
            (request, _) => Task.FromResult(new AgentRunResult(AgentRunOutcome.Succeeded, "pod", 0, "ok", null, null));

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return OnRun(request, cancellationToken);
        }

        public Task CancelAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Cancels.Add(request);
            return Task.CompletedTask;
        }
    }

    private static AgentPodActivities Activities(FakeRunner runner, TimeSpan? heartbeat = null) =>
        new(runner, new AgentPodsOptions { HeartbeatInterval = heartbeat ?? TimeSpan.FromSeconds(15) }, NullLogger<AgentPodActivities>.Instance);

    private static ActivityEnvironment Environment(Action<object?[]>? heartbeat = null) =>
        new()
        {
            Info = ActivityEnvironment.DefaultInfo with { WorkflowId = "spike-1", WorkflowRunId = "run-1" },
            Heartbeater = details => heartbeat?.Invoke(details),
        };

    [Fact]
    public async Task RunAgentPod_BuildsTheRunFromTheActivityInfoAndInput()
    {
        var runner = new FakeRunner();

        var result = await Environment().RunAsync(() => Activities(runner).RunAgentPodAsync(Input));

        var request = Assert.Single(runner.Runs);
        Assert.Equal(new AgentRunRequest("spike-1", "run-1", "claude", "Say hello", AgentTier.Read, 1800), request);
        Assert.Equal(AgentRunOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task DeleteAgentPod_DeletesThePodOfTheCurrentRunAndStep()
    {
        var runner = new FakeRunner();

        await Environment().RunAsync(() => Activities(runner).DeleteAgentPodAsync("claude"));

        var cancel = Assert.Single(runner.Cancels);
        Assert.Equal("spike-1", cancel.WorkflowId);
        Assert.Equal("run-1", cancel.RunId);
        Assert.Equal("claude", cancel.Step);
    }

    [Fact]
    public async Task RunAgentPod_HeartbeatsWhileThePodRuns()
    {
        var beats = 0;
        var release = new TaskCompletionSource<AgentRunResult>();
        var runner = new FakeRunner { OnRun = (_, _) => release.Task };
        var environment = Environment(_ => Interlocked.Increment(ref beats));

        var run = environment.RunAsync(() => Activities(runner, TimeSpan.FromMilliseconds(20)).RunAgentPodAsync(Input));
        await WaitUntilAsync(() => Volatile.Read(ref beats) >= 3);
        release.SetResult(new AgentRunResult(AgentRunOutcome.Succeeded, "pod", 0, "ok", null, null));
        await run;

        Assert.True(beats >= 3);
    }

    [Fact]
    public async Task RunAgentPod_StopsHeartbeatingOnceTheRunEnds()
    {
        var beats = 0;
        var environment = Environment(_ => Interlocked.Increment(ref beats));

        await environment.RunAsync(() => Activities(new FakeRunner(), TimeSpan.FromMilliseconds(10)).RunAgentPodAsync(Input));
        var atEnd = Volatile.Read(ref beats);
        await Task.Delay(100, Ct);

        Assert.Equal(atEnd, Volatile.Read(ref beats));
    }

    [Fact]
    public async Task RunAgentPod_PassesTheAttemptAndRepoContextToTheRunner()
    {
        var runner = new FakeRunner();
        var repo = new AgentRepoContext("octo/widgets", 7, new string('a', 40));

        await Environment().RunAsync(() => Activities(runner).RunAgentPodAsync(Input with { Attempt = 3, Repo = repo }));

        var request = Assert.Single(runner.Runs);
        Assert.Equal(3, request.Attempt);
        Assert.Equal(repo, request.Repo);
    }

    [Fact]
    public async Task RunAgentPod_WhenCancelledByTheWorkflow_DeletesThePod()
    {
        var runner = new FakeRunner { OnRun = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        var environment = Environment();

        var run = environment.RunAsync(() => Activities(runner).RunAgentPodAsync(Input));
        await WaitUntilAsync(() => runner.Runs.Count == 1);
        environment.Cancel(ActivityCancelReason.CancelRequested);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(runner.Cancels);
    }

    [Theory]
    [InlineData(ActivityCancelReason.WorkerShutdown)]
    [InlineData(ActivityCancelReason.Timeout)]
    [InlineData(ActivityCancelReason.GoneFromServer)]
    [InlineData(ActivityCancelReason.Paused)]
    [InlineData(ActivityCancelReason.Reset)]
    [InlineData(ActivityCancelReason.HeartbeatRecordFailure)]
    public async Task RunAgentPod_WhenCancelledForAnyReasonButTheWorkflow_LeavesThePodForTheRetry(ActivityCancelReason reason)
    {
        var runner = new FakeRunner { OnRun = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        var environment = Environment();

        var run = environment.RunAsync(() => Activities(runner).RunAgentPodAsync(Input));
        await WaitUntilAsync(() => runner.Runs.Count == 1);
        environment.Cancel(reason);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(runner.Cancels);
    }

    [Fact]
    public async Task RunAgentPod_AnAttemptThatTimedOutNeverDeletesThePodTheRetryReattachedTo()
    {
        var api = new FakeKubeApi();
        var podName = AgentPodNaming.PodName("spike-1", "run-1", "claude");
        var held = new PodBehavior { Logs = FakeKubeApi.SuccessLogs };
        api.SetBehavior(podName, held);
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

        var firstAttempt = Environment();
        var first = firstAttempt.RunAsync(() => activities.RunAgentPodAsync(Input));
        await WaitUntilAsync(() => api.Secrets.ContainsKey(podName));
        var secondAttempt = Environment();
        var second = secondAttempt.RunAsync(() => activities.RunAgentPodAsync(Input));
        await WaitUntilAsync(() => api.CountOperations($"POST secret {podName}") == 2);
        firstAttempt.Cancel(ActivityCancelReason.Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        held.Release.SetResult();
        var result = await second;

        Assert.Equal(AgentRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, api.PodsCreated);
        Assert.DoesNotContain($"DELETE pod {podName}", api.Operations);
        Assert.Contains(podName, api.Pods.Keys);
    }

    [Fact]
    public async Task RunAgentPod_WhenTheRunnerFailsTransiently_ThrowsSoTemporalRetries()
    {
        var runner = new FakeRunner { OnRun = (_, _) => throw new HttpRequestException("api down") };

        await Assert.ThrowsAsync<HttpRequestException>(() => Environment().RunAsync(() => Activities(runner).RunAgentPodAsync(Input)));
        Assert.Empty(runner.Cancels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public async Task RunAgentPod_RejectsAnOutOfRangeDeadlineWithoutRetrying(int seconds)
    {
        var runner = new FakeRunner();

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => Environment().RunAsync(() => Activities(runner).RunAgentPodAsync(Input with { DeadlineSeconds = seconds })));

        Assert.True(failure.NonRetryable);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task RunAgentPod_MapsConfigurationErrorsToNonRetryableFailures()
    {
        var runner = new FakeRunner { OnRun = (_, _) => throw new AgentConfigurationException("no cluster account") };

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Environment().RunAsync(() => Activities(runner).RunAgentPodAsync(Input)));

        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public void ActivityTimeout_IsDerivedFromThePodDeadline()
    {
        var timeout = AgentRunTimeouts.ActivityStartToClose(1800);

        Assert.True(timeout > TimeSpan.FromSeconds(1800 + AgentRunTimeouts.SchedulingGraceSeconds));
        Assert.True(AgentRunTimeouts.ActivityStartToClose(3600) > AgentRunTimeouts.ActivityStartToClose(1800));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(10, Ct);
        }
    }
}
