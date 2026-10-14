using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.GitHub;

public class GitHubApiClientApplyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingTokens : IGitHubInstallationTokenProvider
    {
        public List<(string Repo, Dictionary<string, string> Permissions)> Requests { get; } = [];

        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
        {
            Requests.Add((repo, permissions.ToDictionary(p => p.Key, p => p.Value)));
            return Task.FromResult("ghs_install_token");
        }
    }

    private static (GitHubApiClient Client, StubHttpHandler Handler, RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var tokens = new RecordingTokens();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubApiClient(http, tokens, Options.Create(new GitHubAppOptions { AppId = "3233443" })), handler, tokens);
    }

    [Fact]
    public async Task RemoveLabelAsync_DeletesTheEscapedLabelWithAPullRequestsWriteToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("[]"));

        await client.RemoveLabelAsync("octo/widgets", 7, "agent/needs-triage", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/repos/octo/widgets/issues/7/labels/agent%2Fneeds-triage", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "write" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task RemoveLabelAsync_TreatsALabelThatIsNotOnThePrAsDone()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"message":"Label does not exist"}""", HttpStatusCode.NotFound));

        await client.RemoveLabelAsync("octo/widgets", 7, "agent/needs-triage", Ct);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RemoveLabelAsync_RaisesOnOtherFailures(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"no"}""", status));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.RemoveLabelAsync("octo/widgets", 7, "x", Ct));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task RemoveLabelAsync_RejectsBadArgumentsBeforeAnyRequest()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("[]"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RemoveLabelAsync("octo/widgets", 0, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLabelAsync("octo/widgets", 7, " ", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLabelAsync("not-a-repo", 7, "x", Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Requests);
    }

    [Fact]
    public async Task ListCommitStatusesAsync_ReadsTheLatestStatusPerContextWithAStatusesReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""
            {"state":"success","statuses":[
              {"context":"renovate/stability-days","state":"success"},
              {"context":"Terraform Cloud/x","state":"pending"}]}
            """));

        var statuses = await client.ListCommitStatusesAsync("octo/widgets", Sha, Ct);

        Assert.Equal([new GitHubCommitStatus("renovate/stability-days", "success"), new GitHubCommitStatus("Terraform Cloud/x", "pending")], statuses);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/octo/widgets/commits/{Sha}/status", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["statuses"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task ListCommitStatusesAsync_ReadsEveryPage()
    {
        var (client, handler, _) = Create(request =>
        {
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            var count = page == 1 ? 100 : 3;
            var items = string.Join(',', Enumerable.Range(0, count).Select(i => $$"""{"context":"c{{page}}-{{i}}","state":"success"}"""));
            return StubHttpHandler.Json($$"""{"state":"success","statuses":[{{items}}]}""");
        });

        var statuses = await client.ListCommitStatusesAsync("octo/widgets", Sha, Ct);

        Assert.Equal(103, statuses.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListCommitStatusesAsync_RejectsANonShaBeforeAnyRequest()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCommitStatusesAsync("octo/widgets", "../x", Ct));

        Assert.Empty(handler.Requests);
    }
}
