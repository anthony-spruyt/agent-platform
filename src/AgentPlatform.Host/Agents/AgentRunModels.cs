using System.Text.Json.Serialization;

namespace AgentPlatform.Host.Agents;

public sealed record AgentRunRequest(
    string WorkflowId,
    string RunId,
    string Step,
    string Prompt,
    AgentTier Tier,
    int DeadlineSeconds);

[JsonConverter(typeof(JsonStringEnumConverter<AgentRunOutcome>))]
public enum AgentRunOutcome
{
    Succeeded,
    Truncated,
    UsageLimit,
    PodFailed,
    TimedOut,
}

public sealed record AgentRunUsage(
    long? InputTokens,
    long? OutputTokens,
    double? CostUsd,
    long? DurationMs,
    int? NumTurns,
    string? SessionId);

public sealed record AgentRunResult(
    AgentRunOutcome Outcome,
    string PodName,
    int? ExitCode,
    string? ResultText,
    string? Detail,
    AgentRunUsage? Usage,
    string? Model = null);

public sealed record AgentRunIdentity(string WorkflowId, string RunId);
