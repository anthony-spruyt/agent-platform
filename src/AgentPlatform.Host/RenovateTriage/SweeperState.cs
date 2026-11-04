namespace AgentPlatform.Host.RenovateTriage;

public sealed class SweeperState
{
    public int Id { get; set; }

    public DateTimeOffset? LastSeenDeliveredAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }
}
