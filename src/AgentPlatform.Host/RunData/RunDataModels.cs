namespace AgentPlatform.Host.RunData;

public enum RunStatus
{
    Running,
    Succeeded,
    Failed,
}

public enum LadderRung
{
    Rule,
    Jev,
    Claude,
}

public sealed class Prompt
{
    public required string Hash { get; set; }

    public required string Name { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Run
{
    public Guid Id { get; set; }

    public required string WorkflowId { get; set; }

    public required string WorkflowRunId { get; set; }

    public required string WorkflowType { get; set; }

    public RunStatus Status { get; set; }

    public required string InputEvent { get; set; }

    public string? Context { get; set; }

    public string? PromptHash { get; set; }

    public string? Model { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? DurationMs { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public decimal? CostUsd { get; set; }

    public string? Outcome { get; set; }

    public string? OutcomeDetail { get; set; }

    public DateTimeOffset? OutcomeRecordedAt { get; set; }

    public List<RunStep> Steps { get; } = [];
}

public sealed class RunStep
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public int Seq { get; set; }

    public required string Name { get; set; }

    public LadderRung Rung { get; set; }

    public string? PromptHash { get; set; }

    public string? Model { get; set; }

    public string? Input { get; set; }

    public string? Output { get; set; }

    public double? Confidence { get; set; }

    public required string StopReason { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public decimal? CostUsd { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public long DurationMs { get; set; }
}
