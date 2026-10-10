using System.Text.Json.Serialization;
using AgentPlatform.Host.Agents;
using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

public sealed record SpikeInput(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("tier")] AgentTier Tier);

[JsonConverter(typeof(JsonStringEnumConverter<SpikeStatus>))]
public enum SpikeStatus
{
    Succeeded,
    AgentFailed,
    ResultTimeout,
}

public sealed record SpikeResult(
    SpikeStatus Status,
    SubmitResultPayload? Result,
    AgentRunResult Agent,
    AgentTier Tier,
    string WorkflowId,
    string RunId);

[Workflow]
public class SpikeWorkflow
{
    public const string SubmitResultSignal = "submit_result";

    public static readonly TimeSpan ResultSignalTimeout = TimeSpan.FromMinutes(2);

    private SubmitResultPayload? _result;

    [WorkflowSignal(SubmitResultSignal)]
    public Task SubmitResultAsync(SubmitResultPayload payload)
    {
        _result ??= payload;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<SpikeResult> RunAsync(SpikeInput input)
    {
        var deadline = AgentRunTimeouts.DefaultPodDeadlineSeconds;
        var agent = await Workflow.ExecuteActivityAsync(
            (AgentPodActivities activities) => activities.RunAgentPodAsync(new RunAgentPodInput("spike", input.Prompt, input.Tier, deadline)),
            new ActivityOptions
            {
                TaskQueue = TaskQueues.AgentPods,
                StartToCloseTimeout = AgentRunTimeouts.ActivityStartToClose(deadline),
                HeartbeatTimeout = TimeSpan.FromSeconds(60),
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                RetryPolicy = new RetryPolicy
                {
                    InitialInterval = TimeSpan.FromSeconds(1),
                    BackoffCoefficient = 2,
                    MaximumInterval = TimeSpan.FromMinutes(1),
                    MaximumAttempts = 5,
                },
            });

        var info = Workflow.Info;
        if (agent.Outcome != AgentRunOutcome.Succeeded)
        {
            return new SpikeResult(SpikeStatus.AgentFailed, _result, agent, input.Tier, info.WorkflowId, info.RunId);
        }

        var arrived = await Workflow.WaitConditionAsync(() => _result is not null, ResultSignalTimeout);
        return new SpikeResult(
            arrived ? SpikeStatus.Succeeded : SpikeStatus.ResultTimeout,
            _result,
            agent,
            input.Tier,
            info.WorkflowId,
            info.RunId);
    }
}
