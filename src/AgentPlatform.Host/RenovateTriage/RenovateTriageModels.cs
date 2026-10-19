using System.Text.Json.Serialization;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RunData;

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
    NeedsPrecheck,
    CiFailed,
    Climb,
}

[JsonConverter(typeof(JsonStringEnumConverter<RenovateTriageOutcome>))]
public enum RenovateTriageOutcome
{
    Skipped,
    AlreadyJudged,
    Judged,
    Parked,
}

public sealed record RenovateTriageInput(string Repo, int PrNumber);

public sealed record AssessInput(string Repo, int PrNumber, bool Step2 = false, string? PrecheckedSha = null, bool Claude = false);

public sealed record AssessResult(AssessStatus Status, string HeadSha, string? Reason = null, TriageDecision? Decision = null, TriageMode Mode = TriageMode.Shadow, bool Stored = false, bool StatusPending = false);

[JsonConverter(typeof(JsonStringEnumConverter<PrecheckVerdict>))]
public enum PrecheckVerdict
{
    Passed,
    DiffNotVersionOnly,
    BodyFlagged,
    BodyUnchecked,
}

public sealed record PrecheckInput(string Repo, int PrNumber, string HeadSha);

public sealed record PrecheckResult(bool HeadMoved, PrecheckVerdict Verdict, ScannedText? Body = null, string? Detail = null);

public sealed record JudgeInput(
    string Repo,
    int PrNumber,
    string HeadSha,
    bool RetryJevErrors = false,
    ScannedText? Body = null,
    IReadOnlyList<ScannedText>? Changelogs = null);

public sealed record JudgeResult(bool HeadMoved, TriageDecision? Decision = null, JevAnswers? Jev = null, string? JevError = null);

public sealed record RecordVerdictInput(string Repo, int PrNumber, string HeadSha, TriageDecision Decision, TriageMode Mode = TriageMode.Shadow, LadderRung? Rung = null);

public sealed record RecordVerdictResult(bool Inserted, TriageMode Mode);

public sealed record ApplyInput(string Repo, int PrNumber, string HeadSha, TriageDecision Decision, TriageMode Mode, bool CiWaitOver = false);

[JsonConverter(typeof(JsonStringEnumConverter<ApplyOutcome>))]
public enum ApplyOutcome
{
    Applied,
    Aborted,
    Refused,
}

public sealed record ApplyResult(ApplyOutcome Outcome, string? Reason = null, TriageDecision? Effective = null, bool StatusPending = false);

public sealed record RenovateTriageResult(RenovateTriageOutcome Outcome, string HeadSha, string? Reason, TriageVerdict? Verdict);

[JsonConverter(typeof(JsonStringEnumConverter<CiFailureKind>))]
public enum CiFailureKind
{
    Flaky,
    Real,
    Unsure,
}
