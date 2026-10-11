using System.Text;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.GitHub;

public class CheckSuiteEventTests
{
    private static IReadOnlyList<AgentPlatform.Host.Workflows.CheckSuiteCompletedSignal> Extract(string body, string delivery = "d-1") =>
        CheckSuiteEvent.ToSignals(Encoding.UTF8.GetBytes(body), delivery);

    [Fact]
    public void ACompletedRenovateSuiteWithAPullRequest_ProducesOneSignalWithEveryField()
    {
        var signal = Assert.Single(Extract(GitHubPayloads.CheckSuite(appSlug: "circleci", conclusion: "failure", suiteId: 1234), "delivery-9"));

        Assert.Equal(GitHubPayloads.Repo, signal.Repo);
        Assert.Equal(7, signal.PrNumber);
        Assert.Equal(GitHubPayloads.HeadSha, signal.HeadSha);
        Assert.Equal(1234, signal.CheckSuiteId);
        Assert.Equal("circleci", signal.AppSlug);
        Assert.Equal("failure", signal.Conclusion);
        Assert.Equal("delivery-9", signal.DeliveryId);
    }

    [Theory]
    [InlineData("github-actions")]
    [InlineData("sonarqubecloud")]
    [InlineData("some-other-app")]
    public void AnyAppIsAccepted(string slug)
    {
        Assert.Single(Extract(GitHubPayloads.CheckSuite(appSlug: slug)));
    }

    [Fact]
    public void ASuiteWithoutAnApp_IsStillForwarded()
    {
        var signal = Assert.Single(Extract(GitHubPayloads.CheckSuite(appSlug: null)));

        Assert.Null(signal.AppSlug);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("rerequested")]
    [InlineData("created")]
    [InlineData("")]
    public void AnActionOtherThanCompleted_IsDropped(string action)
    {
        Assert.Empty(Extract(GitHubPayloads.CheckSuite(action: action)));
    }

    [Fact]
    public void ASuiteWithNoPullRequests_IsDropped()
    {
        Assert.Empty(Extract(GitHubPayloads.CheckSuite(pullRequests: [])));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/renovate/foo")]
    [InlineData("Renovate/foo")]
    [InlineData("renovate")]
    [InlineData("renovate-foo")]
    [InlineData("renovate/configure")]
    [InlineData("")]
    [InlineData(null)]
    public void ABranchThatIsNotARenovateUpdateBranch_IsDropped(string? branch)
    {
        Assert.Empty(Extract(GitHubPayloads.CheckSuite(branch: branch)));
    }

    [Theory]
    [InlineData("renovate/foo")]
    [InlineData("renovate/major-dotnet")]
    [InlineData("renovate/configure-2")]
    [InlineData("renovate/configure/x")]
    public void ARenovateBranchOtherThanConfigure_IsForwarded(string branch)
    {
        Assert.Single(Extract(GitHubPayloads.CheckSuite(branch: branch)));
    }

    [Fact]
    public void APullRequestWhoseHeadIsAnotherRepository_IsDropped()
    {
        Assert.Empty(Extract(GitHubPayloads.CheckSuite(pullRequests: [GitHubPayloads.PullRequest(7, headRepoId: 1)])));
    }

    [Fact]
    public void OnlyTheSameRepositoryPullRequestsOfASuiteAreForwarded()
    {
        var signals = Extract(GitHubPayloads.CheckSuite(pullRequests:
        [
            GitHubPayloads.PullRequest(7),
            GitHubPayloads.PullRequest(8, headRepoId: 1),
            GitHubPayloads.PullRequest(9),
        ]));

        Assert.Equal([7, 9], signals.Select(s => s.PrNumber));
    }

    [Fact]
    public void APullRequestWithoutAHeadRepository_IsDropped()
    {
        var body = """
            {"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","head_sha":"abc",
            "pull_requests":[{"number":3,"head":{"ref":"renovate/foo","sha":"abc","repo":null}}]},
            "repository":{"id":4242,"full_name":"octo/widgets"}}
            """;

        Assert.Empty(Extract(body));
    }

    [Theory]
    [InlineData("""{"action":"completed"}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","head_sha":"abc","pull_requests":[{"number":3,"head":{"repo":{"id":1}}}]}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","pull_requests":[{"number":3,"head":{"repo":{"id":1}}}]},"repository":{"id":1,"full_name":"o/r"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","head_sha":"abc","pull_requests":[{"number":0,"head":{"repo":{"id":1}}}]},"repository":{"id":1,"full_name":"o/r"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","head_sha":"abc","pull_requests":[{"number":3,"head":{"repo":{"id":1}}}]},"repository":{"id":1,"full_name":"../etc"}}""")]
    [InlineData("""{"action":"completed","check_suite":{"id":1,"head_branch":"renovate/foo","head_sha":"abc","pull_requests":[{"number":3,"head":{"repo":{"id":1}}}]},"repository":{"id":1,"full_name":"o/r/extra"}}""")]
    public void AnIncompletePayload_IsDropped(string body)
    {
        Assert.Empty(Extract(body));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void ABodyThatIsNotAnObject_Throws(string body)
    {
        Assert.ThrowsAny<JsonException>(() => Extract(body));
    }
}
