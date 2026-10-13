using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.PullRequests;

public class CheckRunReporterTests
{
    private const string ExternalId = "renovate-triage/anthony-spruyt/widgets/7@" + FakeGitHubApi.Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CheckRunReport Report(
        CheckRunStatus status = CheckRunStatus.InProgress,
        CheckRunConclusion? conclusion = null,
        string summary = "Waiting for CI",
        string externalId = ExternalId) =>
        new("anthony-spruyt/widgets", FakeGitHubApi.Sha, "Agent triage", externalId, status, conclusion, "Agent triage", summary);

    [Fact]
    public async Task AFirstReport_CreatesTheRunWithTheStatusAndText()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);

        await reporter.ReportAsync(Report(), Ct);

        var (_, run) = Assert.Single(github.StoredCheckRuns);
        Assert.Equal(new GitHubCheckRunWrite("Agent triage", FakeGitHubApi.Sha, ExternalId, "in_progress", null, "Agent triage", "Waiting for CI"), run);
        Assert.Equal(["check-create:700:in_progress"], github.CheckRunWrites);
    }

    [Fact]
    public async Task ALaterReportOnAnOpenRun_UpdatesItInsteadOfCreatingAnother()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);
        await reporter.ReportAsync(Report(), Ct);

        await reporter.ReportAsync(Report(summary: "Checking the diff"), Ct);

        var (id, run) = Assert.Single(github.StoredCheckRuns);
        Assert.Equal("Checking the diff", run.Summary);
        Assert.Equal(["check-create:700:in_progress", $"check-update:{id}:in_progress"], github.CheckRunWrites);
    }

    [Theory]
    [InlineData(CheckRunConclusion.Success, "success")]
    [InlineData(CheckRunConclusion.Failure, "failure")]
    [InlineData(CheckRunConclusion.Neutral, "neutral")]
    [InlineData(CheckRunConclusion.Skipped, "skipped")]
    public async Task ACompletedReport_CompletesTheOpenRunWithTheConclusion(CheckRunConclusion conclusion, string expected)
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);
        await reporter.ReportAsync(Report(), Ct);

        await reporter.ReportAsync(Report(CheckRunStatus.Completed, conclusion, "Verdict applied"), Ct);

        var (_, run) = Assert.Single(github.StoredCheckRuns);
        Assert.Equal("completed", run.Status);
        Assert.Equal(expected, run.Conclusion);
        Assert.Equal("Verdict applied", run.Summary);
    }

    [Fact]
    public async Task ACompletedReportWithNoRunYet_CreatesItCompleted()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);

        await reporter.ReportAsync(Report(CheckRunStatus.Completed, CheckRunConclusion.Skipped, "Skipped"), Ct);

        var (_, run) = Assert.Single(github.StoredCheckRuns);
        Assert.Equal("completed", run.Status);
        Assert.Equal("skipped", run.Conclusion);
    }

    [Theory]
    [InlineData(CheckRunStatus.InProgress, null)]
    [InlineData(CheckRunStatus.Queued, null)]
    [InlineData(CheckRunStatus.Completed, CheckRunConclusion.Failure)]
    public async Task ACompletedRun_IsNeverReopenedOrRewritten(CheckRunStatus status, CheckRunConclusion? conclusion)
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);
        await reporter.ReportAsync(Report(CheckRunStatus.Completed, CheckRunConclusion.Success, "Verdict applied"), Ct);
        github.CheckRunWrites.Clear();

        await reporter.ReportAsync(Report(status, conclusion, "A later run"), Ct);

        Assert.Empty(github.CheckRunWrites);
        var (_, run) = Assert.Single(github.StoredCheckRuns);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal("Verdict applied", run.Summary);
    }

    [Fact]
    public async Task ARunWithAnotherExternalIdOnTheSameSha_IsLeftAlone()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);
        await reporter.ReportAsync(Report(externalId: "renovate-triage/anthony-spruyt/widgets/8@" + FakeGitHubApi.Sha), Ct);

        await reporter.ReportAsync(Report(), Ct);

        Assert.Equal(2, github.StoredCheckRuns.Count);
        Assert.All(github.StoredCheckRuns, c => Assert.Equal("in_progress", c.Run.Status));
    }

    [Fact]
    public async Task ARetryAfterALostCreateResponse_FindsTheRunAndUpdatesIt()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);
        await github.CreateCheckRunAsync("anthony-spruyt/widgets", new GitHubCheckRunWrite("Agent triage", FakeGitHubApi.Sha, ExternalId, "in_progress", null, "Agent triage", "Waiting for CI"), Ct);

        await reporter.ReportAsync(Report(summary: "Checking the diff"), Ct);

        Assert.Single(github.StoredCheckRuns);
        Assert.Equal("check-update:700:in_progress", github.CheckRunWrites.Last());
    }

    [Theory]
    [InlineData(CheckRunStatus.Completed, null)]
    [InlineData(CheckRunStatus.InProgress, CheckRunConclusion.Success)]
    [InlineData(CheckRunStatus.Queued, CheckRunConclusion.Failure)]
    public async Task AConclusionThatDoesNotMatchTheStatus_IsRefusedBeforeAnyCall(CheckRunStatus status, CheckRunConclusion? conclusion)
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);

        await Assert.ThrowsAsync<ArgumentException>(() => reporter.ReportAsync(Report(status, conclusion), Ct));

        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task AGitHubFailure_Propagates()
    {
        var github = new FakeGitHubApi { Failure = new GitHubApiException("down") };
        var reporter = new CheckRunReporter(github);

        await Assert.ThrowsAsync<GitHubApiException>(() => reporter.ReportAsync(Report(), Ct));
    }

    [Fact]
    public async Task TheQueuedStatus_IsSentAsQueued()
    {
        var github = new FakeGitHubApi();
        var reporter = new CheckRunReporter(github);

        await reporter.ReportAsync(Report(CheckRunStatus.Queued, summary: "Queued for agent review: caps full, next try after 12:00"), Ct);

        Assert.Equal("queued", Assert.Single(github.StoredCheckRuns).Run.Status);
    }
}
