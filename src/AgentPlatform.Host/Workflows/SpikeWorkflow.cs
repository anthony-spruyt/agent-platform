using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
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
    InvalidResult,
}

public sealed record SpikeResult(
    SpikeStatus Status,
    SubmitResultPayload? Result,
    AgentRunResult Agent,
    AgentTier Tier,
    string WorkflowId,
    string RunId,
    TriageResult? Triage = null);

[Workflow]
public partial class SpikeWorkflow
{
    public const string InvalidInputErrorType = "InvalidInput";

    private const string AgentStep = "spike";

    private const int RunAttempt = 1;

    private const string ContextPatch = "spike-triage-context-v1";

    private static readonly ActivityOptions _loadPromptOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(30),
        },
    };

    private readonly AgentPodStep _podStep;

    private SubmitResultPayload? _result;
    private TriageResult? _triage;
    private string? _triageHeadSha;
    private TriageContext? _context;

    public SpikeWorkflow() => _podStep = new AgentPodStep(AgentStep, RunAttempt, ParseSubmitted);

    [WorkflowQuery(TriageContextQueries.GetTriageContext)]
    public TriageContext? GetTriageContext() => _context;

    [WorkflowUpdate(AgentResultUpdates.SubmitResult)]
    public Task SubmitResultAsync(SubmitResultPayload payload) => _podStep.AcceptAsync(payload);

    [WorkflowUpdateValidator(nameof(SubmitResultAsync))]
    public void ValidateSubmitResult(SubmitResultPayload payload) => AgentPodStep.Validate(_podStep, payload);

    [WorkflowRun]
    public async Task<SpikeResult> RunAsync(SpikeInput input)
    {
        var info = Workflow.Info;
        var started = Workflow.UtcNow;
        var (promptName, template, repo) = await ResolvePromptAsync(input);
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
            if (_triageHeadSha is not null && repo is not null && Workflow.Patched(ContextPatch))
            {
                await BuildContextAsync(repo, input.PullRequest);
            }

            var agentStarted = Workflow.UtcNow;
            var outcome = await _podStep.RunAsync(prompt, input.Tier, repo, AgentRunTimeouts.DefaultPodDeadlineSeconds, Workflow.CancellationToken);
            var agent = outcome.Agent;
            var status = ToSpikeStatus(outcome.Status);
            var succeeded = status == SpikeStatus.Succeeded;

            if (run is not null)
            {
                await _podStep.RecordAsync(run, 1, outcome, agentStarted, RunRecorder.ToJson(new { tier = input.Tier }), succeeded ? (object?)_triage ?? _result : null);
                await RunRecorder.CompleteAsync(new CompleteRunInput(
                    run.RunId,
                    succeeded ? RunStatus.Succeeded : RunStatus.Failed,
                    Workflow.UtcNow,
                    agent.Model));
            }

            return new SpikeResult(status, succeeded ? _result : null, agent, input.Tier, info.WorkflowId, info.RunId, succeeded ? _triage : null);
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

    private async Task BuildContextAsync(AgentRepoContext repo, int pullRequest)
    {
        try
        {
            var built = await Workflow.ExecuteActivityAsync(
                (RenovateTriageCodeActivities activities) => activities.BuildContextAsync(new BuildContextInput(repo.Repo, pullRequest, repo.HeadSha, null)),
                RenovateTriageWorkflow.ScanOptions);
            _context = built.Context;
        }
        catch (ActivityFailureException ex) when (!TemporalException.IsCanceledException(ex))
        {
            LogContextFailed(Workflow.Logger, ex);
        }
    }

    private static SpikeStatus ToSpikeStatus(AgentPodStepStatus status) => status switch
    {
        AgentPodStepStatus.Succeeded => SpikeStatus.Succeeded,
        AgentPodStepStatus.ResultTimeout => SpikeStatus.ResultTimeout,
        AgentPodStepStatus.InvalidResult => SpikeStatus.InvalidResult,
        _ => SpikeStatus.AgentFailed,
    };

    private string? ParseSubmitted(SubmitResultPayload payload)
    {
        if (_triageHeadSha is null)
        {
            _result = payload;
            return null;
        }

        if (!TriageResult.TryParse(payload, _triageHeadSha, new StatedVersions(null, null), out var parsed, out var error))
        {
            return error;
        }

        _triage = parsed;
        return null;
    }

    private static async Task<(string PromptName, string Template, AgentRepoContext? Repo)> ResolvePromptAsync(SpikeInput input)
    {
        var repo = input.Repo is null ? null : new AgentRepoContext(input.Repo, input.PullRequest, input.HeadSha ?? string.Empty);
        if (repo is not null && (!GitHubRepoName.TryParse(repo.Repo, out _, out _) || !IsFullSha(repo.HeadSha)))
        {
            throw InvalidInput("repo must be owner/name and headSha a 40-character lowercase hex commit.");
        }

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

        var body = await Workflow.ExecuteActivityAsync(
            (PromptActivities activities) => activities.LoadPromptAsync(input.PromptName),
            _loadPromptOptions);
        return (input.PromptName, body, repo);
    }

    private static bool IsFullSha(string value) =>
        value.Length == 40 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static ApplicationFailureException InvalidInput(string message) =>
        new(message, errorType: InvalidInputErrorType, nonRetryable: true);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the triage context; the run starts without it.")]
    private static partial void LogContextFailed(ILogger logger, Exception exception);
}
