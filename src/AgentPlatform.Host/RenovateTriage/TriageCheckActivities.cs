using System.Globalization;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record TriageCheckInput(string Repo, int PrNumber, string HeadSha, CheckRunStatus Status, CheckRunConclusion? Conclusion, string Summary, bool StartNew = false);

public sealed class TriageCheckActivities(ICheckRunReporter reporter)
{
    public const string ReportName = "RenovateTriageReportCheck";
    public const string CheckName = "Agent triage";

    [Activity(ReportName)]
    public async Task ReportAsync(TriageCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            GitHubRepoName.Parse(input.Repo, nameof(input.Repo));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.PrNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.HeadSha);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Summary);
        }
        catch (ArgumentException ex)
        {
            throw new ApplicationFailureException(ex.Message, ex, nonRetryable: true);
        }

        var report = new CheckRunReport(
            input.Repo,
            input.HeadSha,
            CheckName,
            ExternalId(input.Repo, input.PrNumber, input.HeadSha),
            input.Status,
            input.Conclusion,
            CheckName,
            input.Summary,
            input.StartNew);
        await reporter.ReportAsync(report, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
    }

    public static string ExternalId(string repo, int prNumber, string headSha) =>
        string.Create(CultureInfo.InvariantCulture, $"{GitHubApiClient.TriageCheckExternalIdPrefix}{repo}/{prNumber}@{headSha}");
}
