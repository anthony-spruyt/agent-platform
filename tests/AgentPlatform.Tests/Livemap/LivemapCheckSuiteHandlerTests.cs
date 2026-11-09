using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapCheckSuiteHandlerTests
{
    private const string Fork = "octo/livemap";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class FakeWakeStarter : ILivemapWakeStarter
    {
        public List<LivemapBuildWake> Started { get; } = [];

        public Exception? Failure { get; set; }

        public Task StartAsync(LivemapBuildWake wake, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Started.Add(wake);
            return Task.CompletedTask;
        }
    }

    private sealed class Rig(bool enabled = true)
    {
        public FakeWakeStarter Starter { get; } = new();

        public async Task<GitHubEventOutcome> DeliverAsync(string body, string eventName = "check_suite")
        {
            var handler = new LivemapCheckSuiteHandler(
                Options.Create(new LivemapOptions { Enabled = enabled, Repo = Fork }),
                Starter,
                NullLogger<LivemapCheckSuiteHandler>.Instance);
            return await handler.HandleAsync(new GitHubWebhookEvent(eventName, "d-1", Encoding.UTF8.GetBytes(body)), Ct);
        }
    }

    private static string Suite(string? branch = "main", string action = "completed", object[]? pullRequests = null, string repo = Fork, long suiteId = 4242) =>
        GitHubPayloads.CheckSuite(action: action, branch: branch, pullRequests: pullRequests ?? [], suiteId: suiteId, repo: repo);

    [Fact]
    public void TheHandler_ListensForCheckSuitesOnly()
    {
        var handler = new LivemapCheckSuiteHandler(Options.Create(new LivemapOptions()), new FakeWakeStarter(), NullLogger<LivemapCheckSuiteHandler>.Instance);

        Assert.True(handler.AcceptedEvents.SetEquals(["check_suite"]));
    }

    [Fact]
    public async Task AFinishedRunOnTheForksMainBranch_WakesTheBuildWithItsCommitAndSuite()
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite());

        Assert.Equal(GitHubEventOutcome.Accepted, outcome);
        Assert.Equal(new LivemapBuildWake(GitHubPayloads.HeadSha, 4242), Assert.Single(rig.Starter.Started));
    }

    [Fact]
    public async Task AFinishedRunWithNoBranch_WakesTheBuild()
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Accepted, await rig.DeliverAsync(Suite(branch: null)));
        Assert.Single(rig.Starter.Started);
    }

    [Theory]
    [InlineData("livemap/build-400")]
    [InlineData("livemap/build-1")]
    [InlineData("livemap/build-0")]
    [InlineData("livemap/build-9")]
    public async Task ASuiteOfTheRobotsOwnBuildPullRequest_WakesTheBuild(string branch)
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite(branch, pullRequests: [GitHubPayloads.PullRequest(5)]));

        Assert.Equal(GitHubEventOutcome.Accepted, outcome);
        Assert.Single(rig.Starter.Started);
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("renovate/foo")]
    [InlineData("livemap/build-")]
    [InlineData("livemap/build-4x")]
    [InlineData("livemap/build-400/more")]
    [InlineData("livemap/other-400")]
    [InlineData("Livemap/build-400")]
    [InlineData("main")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ASuiteOfAPullRequestFromAnotherBranch_IsIgnored(string? branch)
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite(branch, pullRequests: [GitHubPayloads.PullRequest(5)]));

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task ASuiteOfAPullRequestFromAFork_IsIgnored_EvenWithTheRightBranchName()
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite("livemap/build-400", pullRequests: [GitHubPayloads.PullRequest(5, headRepoId: 1)]));

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task ASuiteOfAPullRequestIntoAnotherRepository_IsIgnored()
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite("livemap/build-400", pullRequests: [GitHubPayloads.PullRequest(5, baseRepoId: 1)]));

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
    }

    [Fact]
    public async Task ASuiteListingAPullRequestFromAFork_AmongOthers_IsIgnored()
    {
        var rig = new Rig();

        var outcome = await rig.DeliverAsync(Suite("livemap/build-400", pullRequests: [GitHubPayloads.PullRequest(5), GitHubPayloads.PullRequest(6, headRepoId: 1)]));

        Assert.Equal(GitHubEventOutcome.Ignored, outcome);
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("livemap/build-400")]
    [InlineData("renovate/foo")]
    public async Task AFinishedRunOnAnotherBranchWithoutAPullRequest_IsIgnored(string branch)
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync(Suite(branch)));
        Assert.Empty(rig.Starter.Started);
    }

    [Theory]
    [InlineData("octo/other")]
    [InlineData("other/livemap")]
    [InlineData("octo/livemap-fork")]
    public async Task ARunOfAnotherRepository_IsIgnored(string repo)
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync(Suite(repo: repo)));
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task TheRepositoryNameIsComparedWithoutCase()
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Accepted, await rig.DeliverAsync(Suite(repo: "Octo/LiveMap")));
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("rerequested")]
    [InlineData("created")]
    [InlineData("")]
    public async Task AnActionOtherThanCompleted_IsIgnored(string action)
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync(Suite(action: action)));
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_EveryDeliveryIsIgnored_EvenOneThatIsNotJson()
    {
        var rig = new Rig(enabled: false);

        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync(Suite()));
        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync("{"));
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsMalformed()
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Malformed, await rig.DeliverAsync("{"));
    }

    [Theory]
    [InlineData("""{"action":"completed","check_suite":{"id":0,"head_branch":"main","head_sha":"abc"},"repository":{"id":1,"full_name":"octo/livemap"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":5,"head_branch":"main","head_sha":""},"repository":{"id":1,"full_name":"octo/livemap"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":5,"head_branch":"main"},"repository":{"id":1,"full_name":"octo/livemap"}}""")]
    [InlineData("""{"action":"completed","repository":{"id":1,"full_name":"octo/livemap"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":5,"head_branch":"main","head_sha":"abc"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":5,"head_branch":"livemap/build-400","head_sha":"abc","pull_requests":[{"number":3,"head":{"ref":"livemap/build-400","sha":"abc","repo":{"id":0}},"base":{"ref":"main","repo":{"id":0}}}]},"repository":{"id":0,"full_name":"octo/livemap"}}""")]
    [InlineData("null")]
    public async Task ASuiteWithoutWhatTheWakeNeeds_IsIgnored(string body)
    {
        var rig = new Rig();

        Assert.Equal(GitHubEventOutcome.Ignored, await rig.DeliverAsync(body));
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task WhenTheWakeCannotBeStarted_TheDeliveryFails_SoGitHubSendsItAgain()
    {
        var rig = new Rig();
        rig.Starter.Failure = new InvalidOperationException("Temporal is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.DeliverAsync(Suite()));
    }
}
