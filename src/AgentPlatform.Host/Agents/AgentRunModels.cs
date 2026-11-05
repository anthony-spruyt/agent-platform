using System.Text.Json.Serialization;

namespace AgentPlatform.Host.Agents;

public sealed record AgentRepoContext(string Repo, int? PullRequest, string HeadSha);

public sealed record AgentRunOverrides(
    string? Model = null,
    int? MaxTurns = null,
    decimal? MaxBudgetUsd = null,
    IReadOnlyDictionary<string, string>? TokenPermissions = null)
{
    public void Validate()
    {
        if (Model is not null && string.IsNullOrWhiteSpace(Model))
        {
            throw new AgentConfigurationException("The model override must not be blank.");
        }

        if (MaxTurns is < 1)
        {
            throw new AgentConfigurationException("The max turns override must be at least 1.");
        }

        if (MaxBudgetUsd is <= 0)
        {
            throw new AgentConfigurationException("The max budget override must be positive.");
        }

        if (TokenPermissions is not null
            && (TokenPermissions.Count == 0 || TokenPermissions.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value != "read")))
        {
            throw new AgentConfigurationException("The token permission override must name at least one permission, each at the read level.");
        }
    }
}

public sealed record AgentRunRequest(
    string WorkflowId,
    string RunId,
    string Step,
    string Prompt,
    AgentTier Tier,
    int DeadlineSeconds,
    int Attempt = 1,
    AgentRepoContext? Repo = null,
    AgentRunOverrides? Overrides = null);

[JsonConverter(typeof(JsonStringEnumConverter<AgentRunOutcome>))]
public enum AgentRunOutcome
{
    Succeeded,
    Truncated,
    UsageLimit,
    PodFailed,
    TimedOut,
    LimitExceeded,
}

public sealed record AgentRunUsage(
    long? InputTokens,
    long? OutputTokens,
    double? CostUsd,
    long? DurationMs,
    int? NumTurns,
    string? SessionId)
{
    private const double MaxStorableCostUsd = 9_999_999_999d;

    public static decimal? ToCostUsd(double? cost) =>
        cost is { } value && double.IsFinite(value) && Math.Abs(value) <= MaxStorableCostUsd ? (decimal)value : null;
}

public sealed record AgentRunResult(
    AgentRunOutcome Outcome,
    string PodName,
    int? ExitCode,
    string? ResultText,
    string? Detail,
    AgentRunUsage? Usage,
    string? Model = null,
    DateTimeOffset? ResetsAt = null);

public sealed record AgentRunIdentity(string WorkflowId, string RunId, string Step, int Attempt);
