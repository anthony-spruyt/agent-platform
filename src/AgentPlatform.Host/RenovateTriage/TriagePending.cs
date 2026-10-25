using System.Text.Json.Serialization;

namespace AgentPlatform.Host.RenovateTriage;

[JsonConverter(typeof(JsonStringEnumConverter<TriagePendingStatus>))]
public enum TriagePendingStatus
{
    Pending,
    Redriven,
    Settled,
    Owner,
    OwnerHandled,
}

public sealed class TriagePending
{
    public Guid Id { get; set; }

    public required string Repo { get; set; }

    public int PrNumber { get; set; }

    public required string HeadSha { get; set; }

    public required string Reason { get; set; }

    public TriagePendingStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? HeldAt { get; set; }
}
