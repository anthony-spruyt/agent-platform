using System.Globalization;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record TriageCheckInput(string Repo, int PrNumber, string HeadSha, CheckRunStatus Status, CheckRunConclusion? Conclusion, string Summary, bool StartNew = false);

public sealed class TriageCheckActivities(ICheckRunReporter reporter, TimeProvider? time = null)
{
    private const string QueuedPrefix = "Queued for agent review: ";

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
            Title(input),
            input.Summary,
            input.StartNew);
        await reporter.ReportAsync(report, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
    }

    // The PR list shows the status before the dash and this title after it, so a queued run carries its cause and next try here.
    private string Title(TriageCheckInput input) =>
        input.Status == CheckRunStatus.Queued && input.Summary.StartsWith(QueuedPrefix, StringComparison.Ordinal)
            ? input.Summary[QueuedPrefix.Length..].TrimEnd('.').Replace($"after {(time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ", "after ", StringComparison.Ordinal)
            : CheckName;

    public static string ExternalId(string repo, int prNumber, string headSha) =>
        string.Create(CultureInfo.InvariantCulture, $"{GitHubApiClient.TriageCheckExternalIdPrefix}{repo}/{prNumber}@{headSha}");
}
