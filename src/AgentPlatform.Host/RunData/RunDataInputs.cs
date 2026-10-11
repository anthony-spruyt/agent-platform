using System.Text.Json;

namespace AgentPlatform.Host.RunData;

public sealed record BeginRunInput(
    string WorkflowType,
    JsonElement InputEvent,
    DateTimeOffset StartedAt,
    JsonElement? Context = null,
    string? PromptName = null,
    string? PromptBody = null,
    string? Model = null);

public sealed record BeginRunResult(Guid RunId, string? PromptHash);

public sealed record RecordStepInput(
    Guid RunId,
    int Seq,
    string Name,
    LadderRung Rung,
    string StopReason,
    DateTimeOffset StartedAt,
    long DurationMs,
    JsonElement? Input = null,
    JsonElement? Output = null,
    double? Confidence = null,
    string? PromptHash = null,
    string? Model = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? CostUsd = null);

public sealed record CompleteRunInput(Guid RunId, RunStatus Status, DateTimeOffset CompletedAt, string? Model = null);

public sealed class UnknownRunException(Guid runId) : InvalidOperationException($"Run {runId} does not exist.");
