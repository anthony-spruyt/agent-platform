namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageRebase
{
    public Guid Id { get; set; }

    public required string Repo { get; set; }

    public int PrNumber { get; set; }

    public required string HeadSha { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
}
