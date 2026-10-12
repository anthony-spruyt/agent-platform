using System.Text.Json.Serialization;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<TriageMode>))]
public enum TriageMode
{
    Shadow,
    Live,
}

[JsonConverter(typeof(JsonStringEnumConverter<AssessStatus>))]
public enum AssessStatus
{
    Skipped,
    AlreadyJudged,
    Pending,
    Decided,
    NeedsJev,
}

[JsonConverter(typeof(JsonStringEnumConverter<RenovateTriageOutcome>))]
public enum RenovateTriageOutcome
{
    Skipped,
    AlreadyJudged,
    Judged,
}

public sealed record RenovateTriageInput(string Repo, int PrNumber);

public sealed record AssessInput(string Repo, int PrNumber);

public sealed record AssessResult(AssessStatus Status, string HeadSha, string? Reason = null, TriageDecision? Decision = null, TriageMode Mode = TriageMode.Shadow);

public sealed record JudgeInput(string Repo, int PrNumber, string HeadSha, bool RetryJevErrors = false);

public sealed record JudgeResult(bool HeadMoved, TriageDecision? Decision = null, JevAnswers? Jev = null, string? JevError = null);

public sealed record RecordVerdictInput(string Repo, int PrNumber, string HeadSha, TriageDecision Decision, TriageMode Mode = TriageMode.Shadow);

public sealed record RecordVerdictResult(bool Inserted, TriageMode Mode);

public sealed record ApplyInput(string Repo, int PrNumber, string HeadSha, TriageDecision Decision, TriageMode Mode);

[JsonConverter(typeof(JsonStringEnumConverter<ApplyOutcome>))]
public enum ApplyOutcome
{
    Applied,
    Aborted,
    Refused,
}

public sealed record ApplyResult(ApplyOutcome Outcome, string? Reason = null);

public sealed record RenovateTriageResult(RenovateTriageOutcome Outcome, string HeadSha, string? Reason, TriageVerdict? Verdict);
