using System.Text.Json.Serialization;

namespace AgentPlatform.Host.ClaudeBudget;

[JsonConverter(typeof(JsonStringEnumConverter<ClaudeRunOutcome>))]
public enum ClaudeRunOutcome
{
    Succeeded,
    PodFailed,
    TimedOut,
    Truncated,
    NoResult,
    InvalidResult,
    LimitStop,
    Cancelled,
    UsageLimit,
    NotStarted,
}

public static class ClaudeRunOutcomes
{
    // Cancelled (the work moved on) and NotStarted (no pod) say nothing about the revision; UsageLimit counts as a try.
    public static bool IsFailure(this ClaudeRunOutcome outcome) => outcome is not (ClaudeRunOutcome.Succeeded or ClaudeRunOutcome.Cancelled or ClaudeRunOutcome.NotStarted);
}

[JsonConverter(typeof(JsonStringEnumConverter<ClaudeReservationKind>))]
public enum ClaudeReservationKind
{
    Reserved,
    Park,
    Owner,
}

public static class ClaudeBudgetCauses
{
    public const string HourlyCap = "hourly-cap";
    public const string DailyCap = "daily-cap";

    // The wire value is shown to people, so it stays as it was when the cap was per pull request.
    public const string PrDailyCap = "pr-daily-cap";
    public const string BreakerOpen = "breaker-open";
    public const string AttemptsExhausted = "attempts-exhausted";
    public const string UsageLimit = "usage-limit";
}

public sealed record ClaudeReservation(ClaudeReservationKind Kind, Guid? RunId, int Attempt, string? Cause, DateTimeOffset? Until)
{
    public static ClaudeReservation Reserved(Guid runId, int attempt) => new(ClaudeReservationKind.Reserved, runId, attempt, null, null);

    public static ClaudeReservation Park(string cause, DateTimeOffset until) => new(ClaudeReservationKind.Park, null, 0, cause, until);

    public static ClaudeReservation Owner(string cause) => new(ClaudeReservationKind.Owner, null, 0, cause, null);
}

// Subject caps count per Kind; Scope groups the breaker and the scoped cooldown; failed attempts count per Revision.
public sealed record ReserveClaudeRunInput(Guid RunId, string Kind, string Subject, string Scope, string? Revision, string Model, bool ExemptFromGlobalCaps = false);

public sealed record RedriveCandidate(string Kind, string Subject, string Scope, bool ExemptFromGlobalCaps = false);

public sealed record FinishClaudeRunInput(Guid RunId, ClaudeRunOutcome Outcome, string? Model = null, long? InputTokens = null, long? OutputTokens = null, decimal? CostUsd = null);

public sealed record SetClaudeCooldownInput(string Scope, DateTimeOffset? ResetsAt, string Cause);

public sealed class ClaudeRun
{
    public Guid Id { get; set; }

    public required string Kind { get; set; }

    public required string Subject { get; set; }

    public required string Scope { get; set; }

    public string? Revision { get; set; }

    public required string WorkflowId { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset ReservedAt { get; set; }

    public DateTimeOffset? PodCreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public ClaudeRunOutcome? Outcome { get; set; }

    public string? Model { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public decimal? CostUsd { get; set; }
}

public sealed class ClaudeCooldown
{
    public const string Global = "global";

    public required string Scope { get; set; }

    public DateTimeOffset Until { get; set; }

    public required string Cause { get; set; }
}
