using AgentPlatform.Host.PullRequests;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageCheckActivitiesTests
{
    private const string Sha = FakeGitHubApi.Sha;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheStartNewFlag_ReachesTheReporter(bool startNew)
    {
        var reporter = new FakeCheckRunReporter();

        await Run(new TriageCheckActivities(reporter), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.InProgress, null, "Waiting for CI.", startNew));

        Assert.Equal(startNew, Assert.Single(reporter.Reports).StartNew);
    }

    [Theory]
    [InlineData("Queued for agent review: daily-cap, next try after 2030-01-01 15:22 UTC.", "daily-cap, next try after 15:22 UTC")]
    [InlineData("Queued for agent review: daily-cap, next try after 2030-01-02 00:05 UTC.", "daily-cap, next try after 2030-01-02 00:05 UTC")]
    [InlineData("Queued for agent review: usage-limit.", "usage-limit")]
    public async Task AQueuedReport_TitlesTheCauseAndNextTry(string summary, string title)
    {
        var reporter = new FakeCheckRunReporter();

        await Run(new TriageCheckActivities(reporter, new FixedTime(Now)), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.Queued, null, summary));

        var report = Assert.Single(reporter.Reports);
        Assert.Equal(title, report.Title);
        Assert.Equal(summary, report.Summary);
    }

    [Fact]
    public async Task OtherStates_KeepTheCheckNameAsTheirTitle()
    {
        var reporter = new FakeCheckRunReporter();

        await Run(new TriageCheckActivities(reporter, new FixedTime(Now)), new TriageCheckInput("anthony-spruyt/widgets", 7, Sha, CheckRunStatus.InProgress, null, "Queued for agent review: daily-cap."));

        Assert.Equal("Agent triage", Assert.Single(reporter.Reports).Title);
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
