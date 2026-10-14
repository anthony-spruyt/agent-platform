using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Replay;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        new("success", "Looks fine", JsonDocument.Parse("""{"verdict":"safe"}""").RootElement.Clone(), "spike", 1);

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
        new(AgentRunOutcome.Succeeded, "pod-1", 0, "all good", null, new AgentRunUsage(1, 2, 0.1, 3, 4, "s"), "sonnet-test");

    private static async Task<T> InWorkersAsync<T, TPod, TCleanup>(
        WorkflowEnvironment env,
        TPod podActivities,
        TCleanup cleanup,
        Func<Task<T>> body,
        InMemoryRunData? data = null,
        object? runDataActivities = null,
        object? promptActivities = null,
        FakeGitHubApi? github = null)
        where TPod : class
        where TCleanup : class
    {
        var recording = runDataActivities ?? (data ?? InMemoryRunData.Create()).Activities;
        var prompts = promptActivities ?? new PromptActivities(new AgentPromptsOptions(), NullLogger<PromptActivities>.Instance);
        var context = new RenovateTriageCodeActivities(github ?? new FakeGitHubApi(), new FakeJev(), new FakeTextScanner(), Options.Create(new RenovateTriageOptions()));
        using var workflows = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(TaskQueues.AgentPlatform)
                .AddWorkflow<SpikeWorkflow>()
                .AddAllActivities(cleanup)
                .AddAllActivities(context)
                .AddAllActivities(prompts.GetType(), prompts)
                .AddAllActivities(recording.GetType(), recording));
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
            var output = await handle.GetResultAsync();
            await HistoryStore.RecordAsync(handle, "spike-happy-path");
            return output;
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
            Assert.Equal(AgentResultUpdates.ResultAlreadySubmittedErrorType, Assert.IsType<ApplicationFailureException>(rejected.InnerException).ErrorType);
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
    public async Task AResultSubmittedByAnAgentThatThenFails_IsNotReturnedNextToTheFailure()
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
                return new AgentRunResult(AgentRunOutcome.Truncated, "pod-1", 1, null, "cut off", null);
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

        Assert.Equal(SpikeStatus.AgentFailed, result.Status);
        Assert.Null(result.Result);
        Assert.Null(result.Triage);
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
            if (request.RequestUri!.AbsolutePath.EndsWith("/log", StringComparison.Ordinal) && !request.RequestUri.Query.Contains("follow=true", StringComparison.Ordinal) && !failedOnce)
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
            await WaitUntilAsync(() => api.HasOperation($"POST secret {AgentPodNaming.PodName(handle.Id, handle.ResultRunId!, "spike")}"));
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal(1, api.PodCreateAttempts);
        Assert.Equal(1, api.PodsCreated);
        Assert.Empty(api.Pods);
    }

    [Fact]
    public async Task HappyPath_RecordsASucceededRunWithItsPromptAndClaudeStep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();

        await InWorkersAsync(env, new ScriptedActivities(), new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, new SpikeInput("check the repo", AgentTier.Read));
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        }, data);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        Assert.Equal("SpikeWorkflow", run.WorkflowType);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("sonnet-test", run.Model);
        Assert.Equal(PromptHashing.Compute("check the repo"), run.PromptHash);
        Assert.Equal("check the repo", (await db.Prompts.SingleAsync(Ct)).Body);
        Assert.Equal("read", JsonDocument.Parse(run.InputEvent).RootElement.GetProperty("tier").GetString());

        var step = await db.RunSteps.SingleAsync(Ct);
        Assert.Equal(LadderRung.Claude, step.Rung);
        Assert.Equal("submitted", step.StopReason);
        Assert.Equal("sonnet-test", step.Model);
        Assert.Equal(run.PromptHash, step.PromptHash);
        Assert.Equal(1, step.InputTokens);
        Assert.Equal(2, step.OutputTokens);
        Assert.Equal(0.1m, step.CostUsd);
        Assert.Equal("Looks fine", JsonDocument.Parse(step.Output!).RootElement.GetProperty("submitted").GetProperty("summary").GetString());
        Assert.Equal(1, run.InputTokens);
        Assert.Equal(0.1m, run.CostUsd);
    }

    [Fact]
    public async Task WhenNoResultArrives_RecordsAFailedRunStoppedByTheResultTimeout()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();

        await InWorkersAsync(env, new ScriptedActivities(), new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            return await handle.GetResultAsync();
        }, data);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        Assert.Equal("result_timeout", (await db.RunSteps.SingleAsync(Ct)).StopReason);
    }

    [Fact]
    public async Task WhenTheAgentRunFails_RecordsAFailedRunStoppedByTheOutcome()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();
        var activities = new ScriptedActivities
        {
            OnRun = _ => Task.FromResult(new AgentRunResult(AgentRunOutcome.UsageLimit, "pod-1", 1, null, "limit", null)),
        };

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            return await handle.GetResultAsync();
        }, data);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        var step = await db.RunSteps.SingleAsync(Ct);
        Assert.Equal("usage_limit", step.StopReason);
        Assert.Null(step.InputTokens);
    }

    [Fact]
    public async Task WhenTheActivityFailsForGood_RecordsAFailedRunWithoutAStep()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();
        var activities = new ScriptedActivities
        {
            OnRun = _ => throw new ApplicationFailureException("boom", nonRetryable: true),
        };

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        }, data);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        Assert.Empty(db.RunSteps);
    }

    [Fact]
    public async Task WhenTheWorkflowIsCancelled_RecordsAFailedRun()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var data = InMemoryRunData.Create();
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

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await WaitUntilAsync(() => activities.Inputs.Count == 1);
            await handle.CancelAsync();
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        }, data);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WhenRecordingTheRunFails_StillReturnsTheSubmittedResultAfterOneAgentRun(bool beginSucceeds, bool retryable)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var runData = new FailingRunData(beginSucceeds, retryable);
        var activities = new ScriptedActivities();

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            return await handle.GetResultAsync();
        }, runDataActivities: runData);

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Equal("Looks fine", result.Result!.Summary);
        Assert.Single(activities.Inputs);
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
    public async Task WhenCancelledWhileCompletingTheRun_EndsCanceled()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var runData = new BlockingCompleteRunData();

        var status = await InWorkersAsync(env, new ScriptedActivities(), new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            await runData.Blocked.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await handle.CancelAsync();
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return (await handle.DescribeAsync()).Status;
        }, runDataActivities: runData);

        Assert.Equal(WorkflowExecutionStatus.Canceled, status);
    }

    [Fact]
    public async Task WhenTheAgentRunFailsAndRecordingFails_StillFailsWithTheAgentFailure()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities
        {
            OnRun = _ => throw new ApplicationFailureException("boom", nonRetryable: true),
        };

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        }, runDataActivities: new FailingRunData(true, false));
    }

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static SpikeInput TriageInput(string? repo = "octo/widgets", string? headSha = Sha) =>
        new(string.Empty, AgentTier.Read, AgentPrompts.RenovateTriage, repo, 7, headSha);

    private static SubmitResultPayload Triage(string verdict, string headSha = Sha)
    {
        var data = new JsonObject
        {
            ["verdict"] = verdict,
            ["head_sha"] = headSha,
            ["versions"] = new JsonArray(new JsonObject { ["dep"] = "x", ["from"] = "1", ["to"] = "2" }),
            ["fix"] = new JsonArray(),
            ["reasons"] = new JsonArray("r"),
        };
        return new SubmitResultPayload("success", "Safe minor bump", JsonDocument.Parse(data.ToJsonString()).RootElement.Clone(), "spike", 1);
    }

    private static async Task<WorkflowUpdateFailedException> RejectedAsync(
        SpikeInput input,
        SubmitResultPayload bad,
        SubmitResultPayload good)
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

        return await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, input);
            await running.Task;
            var rejected = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(bad)));
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(good));
            release.SetResult();
            await handle.GetResultAsync();
            return rejected;
        });
    }

    [Theory]
    [InlineData("other", 1)]
    [InlineData("spike", 2)]
    public async Task ASubmitFromAnotherStepOrAttempt_IsRejectedAsStale(string step, int attempt)
    {
        var rejected = await RejectedAsync(
            new SpikeInput("p", AgentTier.Read),
            Payload with { Step = step, Attempt = attempt },
            Payload);

        Assert.Equal(AgentResultUpdates.StaleRunErrorType, Assert.IsType<ApplicationFailureException>(rejected.InnerException).ErrorType);
    }

    [Fact]
    public async Task ATriageResultWithTheWrongHeadSha_IsRejectedSoTheAgentCanResubmit()
    {
        var rejected = await RejectedAsync(
            TriageInput(),
            Triage("SAFE", new string('b', 40)),
            Triage("SAFE"));

        var failure = Assert.IsType<ApplicationFailureException>(rejected.InnerException);
        Assert.Equal(AgentResultUpdates.InvalidResultErrorType, failure.ErrorType);
        Assert.Contains("head_sha", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATriageResultWithAnUnknownVerdict_IsRejected()
    {
        var rejected = await RejectedAsync(
            TriageInput(),
            Triage("MAYBE"),
            Triage("SAFE"));

        Assert.Equal(AgentResultUpdates.InvalidResultErrorType, Assert.IsType<ApplicationFailureException>(rejected.InnerException).ErrorType);
    }

    private static async Task<(SpikeResult Result, WorkflowUpdateFailedException[] Rejections)> SubmitAsync(
        SpikeInput input,
        InMemoryRunData data,
        params SubmitResultPayload[] submits)
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

        return await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, input);
            await running.Task;
            var rejections = new List<WorkflowUpdateFailedException>();
            foreach (var submit in submits)
            {
                try
                {
                    await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(submit));
                }
                catch (WorkflowUpdateFailedException ex)
                {
                    rejections.Add(ex);
                }
            }

            release.SetResult();
            return (await handle.GetResultAsync(), rejections.ToArray());
        }, data);
    }

    [Fact]
    public async Task ATriageRun_FailsWhenTheSecondSubmitIsInvalidToo()
    {
        var data = InMemoryRunData.Create();

        var (result, rejections) = await SubmitAsync(TriageInput(), data, Triage("MAYBE"), Triage("NOPE"), Triage("SAFE"));

        Assert.Equal(SpikeStatus.InvalidResult, result.Status);
        Assert.Null(result.Triage);
        Assert.Equal(3, rejections.Length);
        Assert.All(rejections, r => Assert.Equal(AgentResultUpdates.InvalidResultErrorType, Assert.IsType<ApplicationFailureException>(r.InnerException).ErrorType));
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(RunStatus.Failed, (await db.Runs.SingleAsync(Ct)).Status);
        Assert.Equal("invalid_result", (await db.RunSteps.SingleAsync(Ct)).StopReason);
    }

    [Fact]
    public async Task ATriageRun_SucceedsWhenTheResubmitIsValid_AndReturnsTheParsedResult()
    {
        var data = InMemoryRunData.Create();

        var (result, rejections) = await SubmitAsync(TriageInput(), data, Triage("MAYBE"), Triage("SAFE"));

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Single(rejections);
        Assert.Equal("SAFE", result.Triage!.Verdict);
        Assert.Equal(Sha, result.Triage.HeadSha);
        Assert.Null(result.Result);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var submitted = JsonDocument.Parse((await db.RunSteps.SingleAsync(Ct)).Output!).RootElement.GetProperty("submitted");
        Assert.Equal("SAFE", submitted.GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData("octo", Sha)]
    [InlineData("octo/widgets/extra", Sha)]
    [InlineData("octo/wid gets", Sha)]
    [InlineData("octo/widgets", "abc123")]
    [InlineData("octo/widgets", "0123456789ABCDEF0123456789abcdef01234567")]
    [InlineData("octo/widgets", "0123456789abcdef0123456789abcdef0123456z")]
    [InlineData("octo/widgets", "")]
    public async Task ARunWithAMalformedRepoOrHeadSha_FailsBeforeStartingAPod(string repo, string headSha)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput(repo, headSha));
            var failed = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            Assert.Equal(SpikeWorkflow.InvalidInputErrorType, Assert.IsType<ApplicationFailureException>(failed.InnerException).ErrorType);
            return 0;
        });

        Assert.Empty(activities.Inputs);
    }

    [Fact]
    public async Task ATriageRun_RendersThePromptWithTheRepoAndPassesTheRepoContextToThePod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        var result = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput());
            var good = Triage("SAFE");
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(good));
            return await handle.GetResultAsync();
        });

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        var input = Assert.Single(activities.Inputs);
        Assert.Equal(new AgentRepoContext("octo/widgets", 7, Sha), input.Repo);
        Assert.Contains("octo/widgets", input.Prompt, StringComparison.Ordinal);
        Assert.Contains(Sha, input.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", input.Prompt, StringComparison.Ordinal);
        Assert.Equal(1, input.Attempt);
    }

    [Fact]
    public async Task ATriageRun_ExposesTheTriageContextBuiltBeforeThePodStarts()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();
        var github = new FakeGitHubApi();

        var context = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput());
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Triage("SAFE")));
            await handle.GetResultAsync();
            await HistoryStore.RecordAsync(handle, "spike-triage-context");
            return await handle.QueryAsync(wf => wf.GetTriageContext());
        }, github: github);

        Assert.NotNull(context);
        var all = string.Join('\n', context.Sections);
        Assert.Contains("Repository: octo/widgets", all, StringComparison.Ordinal);
        Assert.Contains("kind=\"pr-description\"", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATriageRunWhoseContextCannotBeBuilt_StillRunsThePod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();
        var github = new FakeGitHubApi { Failure = new GitHubApiException("unprocessable", HttpStatusCode.UnprocessableEntity) };

        var (result, context) = await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput());
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Triage("SAFE")));
            return (await handle.GetResultAsync(), await handle.QueryAsync(wf => wf.GetTriageContext()));
        }, github: github);

        Assert.Equal(SpikeStatus.Succeeded, result.Status);
        Assert.Null(context);
        Assert.Single(activities.Inputs);
    }

    [Fact]
    public async Task ARunThatIsNotATriage_HasNoTriageContext()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var github = new FakeGitHubApi();

        var context = await InWorkersAsync(env, new ScriptedActivities(), new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env);
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Payload));
            await handle.GetResultAsync();
            return await handle.QueryAsync(wf => wf.GetTriageContext());
        }, github: github);

        Assert.Null(context);
        Assert.Empty(github.Calls);
    }

    private sealed class StubPrompts(Func<string, string> onLoad)
    {
        public List<string> Loads { get; } = [];

        [Activity("LoadPrompt")]
        public Task<string> LoadPromptAsync(string name)
        {
            Loads.Add(name);
            return Task.FromResult(onLoad(name));
        }
    }

    [Fact]
    public async Task ATriageRun_GetsThePromptBodyFromTheLoadPromptActivityAndRecordsThatBody()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();
        var prompts = new StubPrompts(_ => "override for {{repo}} #{{pr}} at {{head_sha}}");
        var data = InMemoryRunData.Create();

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput());
            await handle.ExecuteUpdateAsync(wf => wf.SubmitResultAsync(Triage("SAFE")));
            var output = await handle.GetResultAsync();
            await HistoryStore.RecordAsync(handle, "spike-named-prompt");
            return output;
        }, data, promptActivities: prompts);

        Assert.Equal(AgentPrompts.RenovateTriage, Assert.Single(prompts.Loads));
        Assert.Equal($"override for octo/widgets #7 at {Sha}", Assert.Single(activities.Inputs).Prompt);
        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        Assert.Equal("override for {{repo}} #{{pr}} at {{head_sha}}", (await db.Prompts.SingleAsync(Ct)).Body);
    }

    [Fact]
    public async Task ATriageRunWhosePromptCannotBeLoaded_FailsBeforeStartingAPod()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();
        var prompts = new StubPrompts(_ => throw new ApplicationFailureException("The prompt override is empty.", nonRetryable: true));

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, TriageInput());
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            return 0;
        }, promptActivities: prompts);

        Assert.Empty(activities.Inputs);
    }

    [Theory]
    [InlineData("no-such-prompt", "octo/widgets")]
    [InlineData("renovate-triage", null)]
    public async Task ARunWithAnUnknownPromptOrNoRepo_FailsBeforeStartingAPod(string promptName, string? repo)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new ScriptedActivities();

        await InWorkersAsync(env, activities, new RecordingCleanup(), async () =>
        {
            var handle = await StartAsync(env, new SpikeInput(string.Empty, AgentTier.Read, promptName, repo, 7, Sha));
            var failed = await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync());
            Assert.Equal(SpikeWorkflow.InvalidInputErrorType, Assert.IsType<ApplicationFailureException>(failed.InnerException).ErrorType);
            return 0;
        });

        Assert.Empty(activities.Inputs);
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
