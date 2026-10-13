using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RunData;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentPlatform.Host.Workflows;

public sealed record SpikeInput(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("tier")] AgentTier Tier,
    [property: JsonPropertyName("promptName")] string? PromptName = null,
    [property: JsonPropertyName("repo")] string? Repo = null,
    [property: JsonPropertyName("pullRequest")] int PullRequest = 0,
    [property: JsonPropertyName("headSha")] string? HeadSha = null);

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
public partial class SpikeWorkflow
{
    public const string InvalidInputErrorType = "InvalidInput";

    public static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(2);

    private const string AgentStep = "spike";

    private const int RunAttempt = 1;

    private SubmitResultPayload? _result;
    private string? _triageHeadSha;

    [WorkflowUpdate(AgentResultUpdates.SubmitResult)]
    public Task SubmitResultAsync(SubmitResultPayload payload)
    {
        _result = payload;
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Step != AgentStep || payload.Attempt != RunAttempt)
        {
            throw new ApplicationFailureException(
                "The result does not belong to the current run step.",
                errorType: AgentResultUpdates.StaleRunErrorType,
                nonRetryable: true);
        }

        if (_result is not null)
        {
            throw new ApplicationFailureException(
                "A result was already submitted for this run.",
                errorType: AgentResultUpdates.ResultAlreadySubmittedErrorType,
                nonRetryable: true);
        }

        if (_triageHeadSha is not null && !TriageResult.TryParse(payload, _triageHeadSha, out _, out var error))
        {
            throw new ApplicationFailureException(error!, errorType: AgentResultUpdates.InvalidResultErrorType, nonRetryable: true);
        }
    }

    [WorkflowRun]
    public async Task<SpikeResult> RunAsync(SpikeInput input)
    {
        var info = Workflow.Info;
        var started = Workflow.UtcNow;
        var (promptName, template, repo) = ResolvePrompt(input);
        var prompt = repo is null ? template : AgentPrompts.Render(template, repo);
        _triageHeadSha = promptName == AgentPrompts.RenovateTriage ? repo?.HeadSha : null;
        var run = await RunRecorder.BeginAsync(new BeginRunInput(
            nameof(SpikeWorkflow),
            RunRecorder.ToJson(input),
            started,
            PromptName: promptName,
            PromptBody: template));

        try
        {
            var agentStarted = Workflow.UtcNow;
            var agent = await RunAgentPodAsync(input, prompt, repo);
            await DeletePodAsync();

            var status = SpikeStatus.AgentFailed;
            if (agent.Outcome == AgentRunOutcome.Succeeded)
            {
                var arrived = await Workflow.WaitConditionAsync(() => _result is not null, ResultTimeout);
                status = arrived ? SpikeStatus.Succeeded : SpikeStatus.ResultTimeout;
            }

            if (run is not null)
            {
                var usage = agent.Usage;
                await RunRecorder.RecordStepAsync(new RecordStepInput(
                    run.RunId,
                    1,
                    AgentStep,
                    LadderRung.Claude,
                    StopReason(status, agent.Outcome),
                    agentStarted,
                    RunRecorder.ElapsedMs(agentStarted),
                    Input: RunRecorder.ToJson(new { tier = input.Tier }),
                    Output: RunRecorder.ToJson(new { outcome = agent.Outcome, resultText = agent.ResultText, detail = agent.Detail, submitted = _result }),
                    PromptHash: run.PromptHash,
                    Model: agent.Model,
                    InputTokens: usage?.InputTokens,
                    OutputTokens: usage?.OutputTokens,
                    CostUsd: usage?.CostUsd is { } cost ? (decimal)cost : null));
                await RunRecorder.CompleteAsync(new CompleteRunInput(
                    run.RunId,
                    status == SpikeStatus.Succeeded ? RunStatus.Succeeded : RunStatus.Failed,
                    Workflow.UtcNow,
                    agent.Model));
            }

            return new SpikeResult(status, _result, agent, input.Tier, info.WorkflowId, info.RunId);
        }
        catch (Exception ex) when (ex is ActivityFailureException or OperationCanceledException)
        {
            if (run is not null)
            {
                await RunRecorder.CompleteAfterFailureAsync(run.RunId);
            }

            throw;
        }
    }

    private static string StopReason(SpikeStatus status, AgentRunOutcome outcome) => status switch
    {
        SpikeStatus.Succeeded => "submitted",
        SpikeStatus.ResultTimeout => "result_timeout",
        _ => JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString()),
    };

    private static (string PromptName, string Template, AgentRepoContext? Repo) ResolvePrompt(SpikeInput input)
    {
        var repo = input.Repo is null ? null : new AgentRepoContext(input.Repo, input.PullRequest, input.HeadSha ?? string.Empty);
        if (input.PromptName is null)
        {
            return repo is null
                ? (AgentStep, input.Prompt, null)
                : throw InvalidInput("A repository run needs a promptName.");
        }

        if (!AgentPrompts.Exists(input.PromptName))
        {
            throw InvalidInput($"Unknown prompt {input.PromptName}.");
        }

        if (repo is null)
        {
            throw InvalidInput("A named prompt needs repo, pullRequest and headSha.");
        }

        return (input.PromptName, AgentPrompts.Load(input.PromptName), repo);
    }

    private static ApplicationFailureException InvalidInput(string message) =>
        new(message, errorType: InvalidInputErrorType, nonRetryable: true);

    private static async Task<AgentRunResult> RunAgentPodAsync(SpikeInput input, string prompt, AgentRepoContext? repo)
    {
        var deadline = AgentRunTimeouts.DefaultPodDeadlineSeconds;
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (AgentPodActivities activities) => activities.RunAgentPodAsync(new RunAgentPodInput(AgentStep, prompt, input.Tier, deadline, RunAttempt, repo)),
                new ActivityOptions
                {
                    TaskQueue = TaskQueues.AgentPods,
                    StartToCloseTimeout = AgentRunTimeouts.ActivityStartToClose(deadline),
                    ScheduleToCloseTimeout = AgentRunTimeouts.ActivityScheduleToClose,
                    HeartbeatTimeout = AgentRunTimeouts.HeartbeatTimeout,
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                    RetryPolicy = new RetryPolicy
                    {
                        InitialInterval = TimeSpan.FromSeconds(1),
                        BackoffCoefficient = 2,
                        MaximumInterval = TimeSpan.FromMinutes(1),
                        MaximumAttempts = 5,
                    },
                });
        }
        catch (Exception ex) when (ex is ActivityFailureException or OperationCanceledException)
        {
            await DeletePodAsync();
            throw;
        }
    }

    private static async Task DeletePodAsync()
    {
        try
        {
            await Workflow.ExecuteLocalActivityAsync(
                (AgentPodActivities activities) => activities.DeleteAgentPodAsync(AgentStep),
                new LocalActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(30),
                    CancellationToken = CancellationToken.None,
                    RetryPolicy = new RetryPolicy { InitialInterval = TimeSpan.FromSeconds(1), MaximumAttempts = 5 },
                });
        }
        catch (ActivityFailureException ex)
        {
            LogPodCleanupFailed(Workflow.Logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the agent pod after the run.")]
    private static partial void LogPodCleanupFailed(ILogger logger, Exception exception);
}
