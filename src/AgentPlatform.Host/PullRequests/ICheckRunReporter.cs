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

// GitHub never reopens a completed run: StartNew makes a queued or in-progress report create a new run when the newest one is completed.
public sealed record CheckRunReport(
    string Repo,
    string HeadSha,
    string Name,
    string ExternalId,
    CheckRunStatus Status,
    CheckRunConclusion? Conclusion,
    string Title,
    string Summary,
    bool StartNew = false);

public interface ICheckRunReporter
{
    Task ReportAsync(CheckRunReport report, CancellationToken cancellationToken);
}
