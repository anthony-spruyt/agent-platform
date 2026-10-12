namespace AgentPlatform.Host.RenovateTriage;

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
}
