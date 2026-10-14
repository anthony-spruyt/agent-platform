using AgentPlatform.Host.RunData;

namespace AgentPlatform.Host.RenovateTriage;

public enum VerdictAction
{
    Approve,
    Comment,
}

public sealed class RenovateTriageVerdict
{
    public Guid Id { get; set; }

    public required string Repo { get; set; }

    public int PrNumber { get; set; }

    public required string HeadSha { get; set; }

    public TriageVerdict Verdict { get; set; }

    public required string Reasons { get; set; }

    public string? Comment { get; set; }

    public TriageMode Mode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public LadderRung? Rung { get; set; }

    public VerdictAction? Action { get; set; }
}
