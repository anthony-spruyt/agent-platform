using System.Text.Json.Serialization;

namespace AgentPlatform.Host.PullRequests;

[JsonConverter(typeof(JsonStringEnumConverter<CheckRunStatus>))]
public enum CheckRunStatus
{
    Queued,
    InProgress,
    Completed,
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckRunConclusion>))]
public enum CheckRunConclusion
{
    Success,
    Failure,
    Neutral,
    Skipped,
}

public sealed record CheckRunReport(
    string Repo,
    string HeadSha,
    string Name,
    string ExternalId,
    CheckRunStatus Status,
    CheckRunConclusion? Conclusion,
    string Title,
    string Summary);

public interface ICheckRunReporter
{
    Task ReportAsync(CheckRunReport report, CancellationToken cancellationToken);
}
