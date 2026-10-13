using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.GitHub;

public class GitHubApiClientCodeRungTests
{
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

    private static readonly IOptions<GitHubAppOptions> AppOptions = Options.Create(new GitHubAppOptions { AppId = "3233443" });

    private static (GitHubApiClient Client, StubHttpHandler Handler, RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var tokens = new RecordingTokens();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubApiClient(http, tokens, AppOptions), handler, tokens);
    }

    private static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    [Fact]
    public async Task GetReleaseByTag_ReadsTheReleaseOfTheSourceRepoWithAContentsReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"tag_name":"v1.2.3","name":"Release 1.2.3","body":"- fixed"}"""));

        var release = await client.GetReleaseByTagAsync("octo/widgets", "upstream/dep", "v1.2.3", Ct);

        Assert.Equal(new GitHubRelease("v1.2.3", "Release 1.2.3", "- fixed"), release);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/repos/upstream/dep/releases/tags/v1.2.3", request.Uri.AbsolutePath);
        Assert.Equal("Bearer ghs_install_token", request.Authorization);
        var (repo, permissions) = Assert.Single(tokens.Requests);
        Assert.Equal("octo/widgets", repo);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, permissions);
    }

    [Fact]
    public async Task GetReleaseByTag_KeepsASlashInAScopedTag()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"tag_name":"x","body":null}"""));

        await client.GetReleaseByTagAsync("octo/widgets", "upstream/dep", "@scope/pkg@1.2.3", Ct);

        Assert.Equal("/repos/upstream/dep/releases/tags/@scope/pkg@1.2.3", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task GetReleaseByTag_WhenThereIsNoSuchRelease_ReturnsNull()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        Assert.Null(await client.GetReleaseByTagAsync("octo/widgets", "upstream/dep", "v9", Ct));
    }

    [Fact]
    public async Task GetReleaseByTag_OnAnyOtherFailure_Throws()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetReleaseByTagAsync("octo/widgets", "upstream/dep", "v1", Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("v1 2")]
    [InlineData("")]
    [InlineData("v1?x=y")]
    public async Task GetReleaseByTag_RejectsATagThatCouldChangeThePath(string tag)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReleaseByTagAsync("octo/widgets", "upstream/dep", tag, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseByTag_RejectsABadSourceRepo()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReleaseByTagAsync("octo/widgets", "upstream/dep/extra", "v1", Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetFileContent_AsksForTheRawFileAtTheRef()
    {
        var (client, handler, tokens) = Create(_ => Text("# Changelog\n\n## 1.2.3\n- fixed"));

        var text = await client.GetFileContentAsync("octo/widgets", "upstream/dep", "CHANGELOG.md", "v1.2.3", 1_000, Ct);

        Assert.Equal("# Changelog\n\n## 1.2.3\n- fixed", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/upstream/dep/contents/CHANGELOG.md", request.Uri.AbsolutePath);
        Assert.Equal("?ref=v1.2.3", request.Uri.Query);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task GetFileContent_SendsTheRawMediaType()
    {
        var accepts = new List<string>();
        var handler = new RawAcceptHandler(accepts);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubApiClient(http, new RecordingTokens(), AppOptions);

        await client.GetFileContentAsync("octo/widgets", "upstream/dep", "CHANGELOG.md", "v1", 100, Ct);

        Assert.Equal(["application/vnd.github.raw+json"], accepts);
    }

    private sealed class RawAcceptHandler(List<string> accepts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            accepts.AddRange(request.Headers.Accept.Select(a => a.MediaType!));
            return Task.FromResult(Text("x"));
        }
    }

    [Fact]
    public async Task GetFileContent_KeepsOnlyTheFirstMaxCharacters()
    {
        var (client, _, _) = Create(_ => Text(new string('a', 50) + new string('b', 50)));

        var text = await client.GetFileContentAsync("octo/widgets", "upstream/dep", "CHANGELOG.md", "v1", 60, Ct);

        Assert.Equal(60, text!.Length);
        Assert.StartsWith(new string('a', 50), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFileContent_WhenTheFileIsMissing_ReturnsNull()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        Assert.Null(await client.GetFileContentAsync("octo/widgets", "upstream/dep", "CHANGELOG.md", "v1", 100, Ct));
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("a/../b")]
    [InlineData("/abs")]
    [InlineData("")]
    public async Task GetFileContent_RejectsAPathThatEscapesTheRepo(string path)
    {
        var (client, handler, _) = Create(_ => Text("x"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileContentAsync("octo/widgets", "upstream/dep", path, "v1", 100, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetJob_ParsesTheRunAndAttemptOfAJob()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":55,"run_id":900,"run_attempt":2,"name":"build","conclusion":"failure"}"""));

        var job = await client.GetJobAsync("octo/widgets", 55, Ct);

        Assert.Equal(new GitHubJob(55, 900, 2, "build", "failure"), job);
        Assert.Equal("/repos/octo/widgets/actions/jobs/55", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task GetJob_ForACheckThatIsNotAnActionsJob_ReturnsNull()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        Assert.Null(await client.GetJobAsync("octo/widgets", 55, Ct));
    }

    [Fact]
    public async Task GetJobLogTail_KeepsTheLastMaxCharacters()
    {
        var log = string.Concat(Enumerable.Range(1, 2000).Select(i => $"line {i}\n"));
        var (client, handler, tokens) = Create(_ => Text(log));

        var tail = await client.GetJobLogTailAsync("octo/widgets", 55, 100, Ct);

        Assert.Equal(100, tail!.Length);
        Assert.EndsWith("line 2000\n", tail, StringComparison.Ordinal);
        Assert.Equal(log[^100..], tail);
        Assert.Equal("/repos/octo/widgets/actions/jobs/55/logs", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task GetJobLogTail_OfAShortLog_ReturnsAllOfIt()
    {
        var (client, _, _) = Create(_ => Text("boom\n"));

        Assert.Equal("boom\n", await client.GetJobLogTailAsync("octo/widgets", 55, 100, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task GetJobLogTail_WhenTheLogIsGone_ReturnsNull(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", status));

        Assert.Null(await client.GetJobLogTailAsync("octo/widgets", 55, 100, Ct));
    }

    [Fact]
    public async Task RerunFailedJobs_PostsToTheRunWithAnActionsWriteToken()
    {
        var (client, handler, tokens) = Create(_ => Text(string.Empty, HttpStatusCode.Created));

        await client.RerunFailedJobsAsync("octo/widgets", 900, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/octo/widgets/actions/runs/900/rerun-failed-jobs", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "write" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task RerunFailedJobs_RejectsABadRunId()
    {
        var (client, handler, _) = Create(_ => Text(string.Empty));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RerunFailedJobsAsync("octo/widgets", 0, Ct));

        Assert.Empty(handler.Requests);
    }
}
