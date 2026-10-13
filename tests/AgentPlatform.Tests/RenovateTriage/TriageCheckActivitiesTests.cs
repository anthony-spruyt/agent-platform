using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageCheckActivitiesTests
{
    private const string Sha = FakeGitHubApi.Sha;

    private static Task Run(TriageCheckActivities activities, TriageCheckInput input) =>
        new ActivityEnvironment().RunAsync(() => activities.ReportAsync(input));

    [Fact]
    public async Task ReportsTheAgentTriageCheck_UnderAnExternalIdTheOwnAppFiltersRecognise()
    {
        var reporter = new FakeCheckRunReporter();

        await Run(new TriageCheckActivities(reporter), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.InProgress, null, "Waiting for CI."));

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("Agent triage", report.Name);
        Assert.Equal($"renovate-triage/anthony-spruyt/widgets/7@{Sha}", report.ExternalId);
        Assert.Equal((CheckRunStatus.InProgress, null), (report.Status, report.Conclusion));
        Assert.Equal("Waiting for CI.", report.Summary);
        Assert.StartsWith(Host.GitHub.GitHubApiClient.TriageCheckExternalIdPrefix, report.ExternalId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompletedReportCarriesItsConclusion()
    {
        var reporter = new FakeCheckRunReporter();

        await Run(new TriageCheckActivities(reporter), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.Completed, CheckRunConclusion.Success, "Verdict applied."));

        Assert.Equal(CheckRunConclusion.Success, Assert.Single(reporter.Reports).Conclusion);
    }

    [Theory]
    [InlineData("not-a-repo", 7, Sha, "summary")]
    [InlineData("anthony-spruyt/widgets", 0, Sha, "summary")]
    [InlineData("anthony-spruyt/widgets", 7, "", "summary")]
    [InlineData("anthony-spruyt/widgets", 7, Sha, " ")]
    public async Task BadInput_FailsWithoutRetry(string repo, int pr, string sha, string summary)
    {
        var reporter = new FakeCheckRunReporter();

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            Run(new TriageCheckActivities(reporter), new TriageCheckInput(repo, pr, sha, CheckRunStatus.InProgress, null, summary)));

        Assert.True(ex.NonRetryable);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task AReporterFailure_IsRetried()
    {
        var reporter = new FakeCheckRunReporter { Failure = new InvalidOperationException("GitHub is down") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new TriageCheckActivities(reporter), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.InProgress, null, "Waiting for CI.")));
    }
}
