using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.PullRequests;

public sealed class CheckRunReporter(IGitHubApiClient github) : ICheckRunReporter
{
    public async Task ReportAsync(CheckRunReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var write = ToWrite(report);

        var existing = await github.FindCheckRunAsync(report.Repo, report.HeadSha, report.Name, report.ExternalId, cancellationToken).ConfigureAwait(false);
        var completed = existing is { Status: "completed" };
        if (existing is null || (completed && report.StartNew && report.Status != CheckRunStatus.Completed))
        {
            await github.CreateCheckRunAsync(report.Repo, write, cancellationToken).ConfigureAwait(false);
        }
        else if (!completed)
        {
            await github.UpdateCheckRunAsync(report.Repo, existing.Id, write, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GitHubCheckRunWrite ToWrite(CheckRunReport report)
    {
        if ((report.Status == CheckRunStatus.Completed) != report.Conclusion.HasValue)
        {
            throw new ArgumentException("A completed check run needs a conclusion, and an open one cannot have one.", nameof(report));
        }

        var status = report.Status switch
        {
            CheckRunStatus.Queued => "queued",
            CheckRunStatus.InProgress => "in_progress",
            CheckRunStatus.Completed => "completed",
            _ => throw new ArgumentOutOfRangeException(nameof(report), report.Status, "Unknown check run status."),
        };
        var conclusion = report.Conclusion switch
        {
            null => null,
            CheckRunConclusion.Success => "success",
            CheckRunConclusion.Failure => "failure",
            CheckRunConclusion.Neutral => "neutral",
            CheckRunConclusion.Skipped => "skipped",
            _ => throw new ArgumentOutOfRangeException(nameof(report), report.Conclusion, "Unknown check run conclusion."),
        };
        return new GitHubCheckRunWrite(report.Name, report.HeadSha, report.ExternalId, status, conclusion, report.Title, report.Summary);
    }
}
