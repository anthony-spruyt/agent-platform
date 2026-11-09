using System.Net;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapGitHubTests
{
    private const string Fork = "octo/livemap";
    private const string Gid = "8886672487109033979";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LivemapGitHub Client, StubHttpHandler Handler, RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.example/") };
        var tokens = new RecordingTokens();
        var client = new LivemapGitHub(http, tokens, Options.Create(new LivemapOptions { Enabled = true, Repo = Fork }));
        return (client, handler, tokens);
    }

    private static HttpResponseMessage RunStarted(long id = 9001) =>
        StubHttpHandler.Json($$"""{"workflow_run_id":{{id}},"run_url":"https://api.github.example/runs/{{id}}","html_url":"https://github.example/runs/{{id}}"}""");

    [Fact]
    public async Task Dispatch_PostsTheBuildToTheWorkflowFile_AndAsksForTheRunDetails()
    {
        var (client, handler, _) = Create(_ => RunStarted(4242));

        var runId = await client.DispatchAsync(LivemapWorkflowFile.Compat, 26430253, Gid, Ct);

        Assert.Equal(4242, runId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/repos/{Fork}/actions/workflows/compat.yaml/dispatches", request.Uri.AbsolutePath);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("main", (string?)body["ref"]);
        Assert.True((bool?)body["return_run_details"]);
        Assert.Equal("26430253", (string?)body["inputs"]!["build_id"]);
        Assert.Equal(Gid, (string?)body["inputs"]!["manifest_gid"]);
    }

    [Fact]
    public async Task Dispatch_UsesTheExeCaptureFile_ForTheCaptureStep()
    {
        var (client, handler, _) = Create(_ => RunStarted());

        await client.DispatchAsync(LivemapWorkflowFile.ExeCapture, 26430253, Gid, Ct);

        Assert.EndsWith("/actions/workflows/exe-capture.yaml/dispatches", Assert.Single(handler.Requests).Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_WhenGitHubAnswers204_IsAnError()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.DispatchAsync(LivemapWorkflowFile.Compat, 26430253, Gid, Ct));

        Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);
    }

    [Theory]
    [InlineData("""{"run_url":"https://api.github.example/runs/1"}""")]
    [InlineData("""{"workflow_run_id":0}""")]
    [InlineData("""{"workflow_run_id":null}""")]
    public async Task Dispatch_WhenTheAnswerHasNoRunId_IsAnError(string answer)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(answer));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.DispatchAsync(LivemapWorkflowFile.Compat, 26430253, Gid, Ct));
    }

    [Fact]
    public async Task Dispatch_WhenGitHubRefuses_IsAnError()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Resource not accessible"}""", HttpStatusCode.Forbidden));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.DispatchAsync(LivemapWorkflowFile.Compat, 26430253, Gid, Ct));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    [Fact]
    public async Task Dispatch_AsksForActionsWriteOnTheForkAlone()
    {
        var (client, _, tokens) = Create(_ => RunStarted());

        await client.DispatchAsync(LivemapWorkflowFile.Compat, 26430253, Gid, Ct);

        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "write" }, minted.Permissions);
    }

    [Theory]
    [InlineData(0, Gid)]
    [InlineData(-5, Gid)]
    [InlineData(26430253, "")]
    [InlineData(26430253, "12ab")]
    [InlineData(26430253, "0123")]
    public async Task Dispatch_RefusesAnInputTheWorkflowCouldReadAsSomethingElse(long buildId, string gid)
    {
        var (client, handler, _) = Create(_ => RunStarted());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.DispatchAsync(LivemapWorkflowFile.Compat, buildId, gid, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetRun_ReadsTheStateConclusionHeadShaAndCheckSuite()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":77,"status":"completed","conclusion":"success","head_sha":"abc123","check_suite_id":5001}"""));

        var run = await client.GetRunAsync(77, Ct);

        Assert.Equal(new LivemapRun(77, "completed", "success", "abc123", 5001), run);
        Assert.True(run!.Succeeded);
        Assert.Equal($"/repos/{Fork}/actions/runs/77", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "read" }, Assert.Single(tokens.Minted).Permissions);
    }

    [Theory]
    [InlineData("queued", null, false, false)]
    [InlineData("in_progress", null, false, false)]
    [InlineData("completed", "failure", true, false)]
    [InlineData("completed", "cancelled", true, false)]
    [InlineData("completed", "success", true, true)]
    public void ARun_IsSuccessfulOnlyWhenCompletedWithSuccess(string status, string? conclusion, bool completed, bool succeeded)
    {
        var run = new LivemapRun(1, status, conclusion, "abc", 5);

        Assert.Equal(completed, run.IsCompleted);
        Assert.Equal(succeeded, run.Succeeded);
    }

    [Fact]
    public async Task GetRun_WithoutACheckSuite_ReportsNone()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"id":77,"status":"queued","head_sha":"abc123"}"""));

        var run = await client.GetRunAsync(77, Ct);

        Assert.Equal(0, run!.CheckSuiteId);
    }

    [Fact]
    public async Task GetRun_WhenTheRunIsGone_IsNull()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetRunAsync(77, Ct));
    }

    [Fact]
    public async Task ListSuiteRuns_ReadsTheEventBranchAndBothRepositoriesOfEachRun()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""
            {"total_count":2,"workflow_runs":[
              {"id":1,"event":"workflow_dispatch","head_branch":"main","repository":{"id":500},"head_repository":{"id":500}},
              {"id":2,"event":"pull_request","head_branch":"livemap/build-26430253","repository":{"id":500},"head_repository":{"id":900}}]}
            """));

        var runs = await client.ListSuiteRunsAsync(5001, Ct);

        Assert.Equal(
            new[] { new LivemapSuiteRun("workflow_dispatch", "main", 500, 500), new LivemapSuiteRun("pull_request", "livemap/build-26430253", 500, 900) },
            runs);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/repos/{Fork}/actions/runs", request.Uri.AbsolutePath);
        Assert.Contains("check_suite_id=5001", request.Uri.Query, StringComparison.Ordinal);
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(new Dictionary<string, string> { ["actions"] = "read" }, minted.Permissions);
    }

    [Fact]
    public async Task ListSuiteRuns_WhenTheSuiteHasNoRun_IsEmpty()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"total_count":0,"workflow_runs":[]}"""));

        Assert.Empty(await client.ListSuiteRunsAsync(5001, Ct));
    }

    [Fact]
    public async Task ListSuiteRuns_WhenARunHasNoHeadRepositoryOrRepository_ReportsNoneForIt()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"workflow_runs":[{"id":1,"event":"pull_request","head_branch":null,"head_repository":null}]}"""));

        Assert.Equal(new LivemapSuiteRun("pull_request", null, 0, 0), Assert.Single(await client.ListSuiteRunsAsync(5001, Ct)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ListSuiteRuns_WhenGitHubDoesNotAnswerTheList_IsAnError(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(status));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.ListSuiteRunsAsync(5001, Ct));

        Assert.Equal(status, failure.StatusCode);
    }

    [Theory]
    [InlineData("""{"total_count":0}""")]
    [InlineData("""{"workflow_runs":null}""")]
    public async Task ListSuiteRuns_WhenTheAnswerHasNoRunList_IsAnError(string answer)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(answer));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ListSuiteRunsAsync(5001, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ListSuiteRuns_RefusesACheckSuiteThatIsNotAnId(long suiteId)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"workflow_runs":[]}"""));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ListSuiteRunsAsync(suiteId, Ct));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("workflow_dispatch", "main", 500, 500, true)]
    [InlineData("workflow_dispatch", "anything", 500, 500, true)]
    [InlineData("pull_request", "livemap/build-26430253", 500, 500, true)]
    [InlineData("workflow_dispatch", "main", 500, 900, false)]
    [InlineData("workflow_dispatch", "main", 500, 0, false)]
    [InlineData("workflow_dispatch", "main", 0, 0, false)]
    [InlineData("pull_request", "main", 500, 500, false)]
    [InlineData("pull_request", "main", 500, 900, false)]
    [InlineData("pull_request", "livemap/build-26430253", 500, 900, false)]
    [InlineData("pull_request", "livemap/build-", 500, 500, false)]
    [InlineData("pull_request", "livemap/build-12x", 500, 500, false)]
    [InlineData("pull_request", null, 500, 500, false)]
    [InlineData("push", "main", 500, 500, false)]
    [InlineData("schedule", "main", 500, 500, false)]
    [InlineData("pull_request_target", "livemap/build-26430253", 500, 500, false)]
    [InlineData("", "main", 500, 500, false)]
    public void ARun_IsTheForksOwnBuildRunOnlyWhenItsHeadIsTheForkAndItIsADispatchOrABuildPullRequest(string runEvent, string? branch, long repositoryId, long headRepositoryId, bool own)
    {
        Assert.Equal(own, new LivemapSuiteRun(runEvent, branch, repositoryId, headRepositoryId).IsOwnBuildRun);
    }

    private static readonly string[] ReportAndBuilds = ["report.json", "builds.json"];

    private static byte[] ZipOf(params string[] names)
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("{\"file\":\"" + name + "\"}");
            }
        }

        return stream.ToArray();
    }

    private static HttpResponseMessage ArtifactList(params (long Id, string Name, bool Expired, long Size)[] artifacts) =>
        StubHttpHandler.Json(new JsonObject
        {
            ["total_count"] = artifacts.Length,
            ["artifacts"] = new JsonArray(artifacts.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["expired"] = a.Expired,
                ["size_in_bytes"] = a.Size,
            }).ToArray()),
        }.ToJsonString());

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static Func<RecordedRequest, HttpResponseMessage> ArtifactFlow(byte[] zip, params (long Id, string Name, bool Expired, long Size)[] artifacts) => request =>
        request.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("/artifacts", StringComparison.Ordinal) => ArtifactList(artifacts),
            var path when path.EndsWith("/zip", StringComparison.Ordinal) => Redirect("https://blob.example/store/blob?sig=abc"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) },
        };

    [Fact]
    public async Task ReadCompatArtifact_FindsTheNamedArtifactOfTheRun_AndReadsItsFiles()
    {
        var (client, handler, tokens) = Create(ArtifactFlow(ZipOf(ReportAndBuilds), (7, "livemap-compat", false, 900)));

        var artifact = await client.ReadCompatArtifactAsync(77, Ct);

        Assert.Equal("""{"file":"report.json"}""", artifact!.ReportJson);
        Assert.Equal("""{"file":"builds.json"}""", artifact.BuildsJson);
        Assert.Equal($"/repos/{Fork}/actions/runs/77/artifacts", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("name=livemap-compat", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Equal($"/repos/{Fork}/actions/artifacts/7/zip", handler.Requests[1].Uri.AbsolutePath);
        Assert.All(tokens.Minted, minted => Assert.Equal(new Dictionary<string, string> { ["actions"] = "read" }, minted.Permissions));
    }

    [Fact]
    public async Task ReadCompatArtifact_NeverSendsTheTokenToTheDownloadAddress()
    {
        var (client, handler, _) = Create(ArtifactFlow(ZipOf("report.json"), (7, "livemap-compat", false, 900)));

        await client.ReadCompatArtifactAsync(77, Ct);

        var download = handler.Requests[^1];
        Assert.Equal("blob.example", download.Uri.Host);
        Assert.Null(download.Authorization);
        Assert.All(handler.Requests.Where(r => r.Uri.Host != "blob.example"), r => Assert.NotNull(r.Authorization));
    }

    [Fact]
    public async Task ReadCompatArtifact_SkipsExpiredArtifactsAndOtherNames()
    {
        var (client, handler, _) = Create(ArtifactFlow(
            ZipOf("report.json"),
            (5, "livemap-compat", true, 900),
            (6, "something-else", false, 900),
            (7, "livemap-compat", false, 900)));

        await client.ReadCompatArtifactAsync(77, Ct);

        Assert.Equal($"/repos/{Fork}/actions/artifacts/7/zip", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task ReadCompatArtifact_WhenNoneIsLeft_IsNull()
    {
        var (client, handler, _) = Create(ArtifactFlow(ZipOf("report.json"), (5, "livemap-compat", true, 900), (6, "something-else", false, 900)));

        Assert.Null(await client.ReadCompatArtifactAsync(77, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReadCompatArtifact_WithTwoLiveArtifactsOfTheName_IsRefused()
    {
        var (client, _, _) = Create(ArtifactFlow(ZipOf("report.json"), (7, "livemap-compat", false, 900), (8, "livemap-compat", false, 900)));

        await Assert.ThrowsAsync<CompatArtifactException>(() => client.ReadCompatArtifactAsync(77, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Gone, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    public async Task ReadCompatArtifact_WhenTheDownloadIsGone_IsNull(HttpStatusCode status, bool atTheRedirectTarget)
    {
        var (client, _, _) = Create(request => request.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("/artifacts", StringComparison.Ordinal) => ArtifactList((7, "livemap-compat", false, 900)),
            var path when path.EndsWith("/zip", StringComparison.Ordinal) && atTheRedirectTarget => Redirect("https://blob.example/store/blob?sig=abc"),
            _ => new HttpResponseMessage(status),
        });

        Assert.Null(await client.ReadCompatArtifactAsync(77, Ct));
    }

    [Theory]
    [InlineData("http://blob.example/store/blob")]
    [InlineData("ftp://blob.example/store/blob")]
    [InlineData("/store/blob")]
    public async Task ReadCompatArtifact_RefusesToFollowARedirectOffHttps(string location)
    {
        var (client, handler, _) = Create(request => request.Uri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal)
            ? ArtifactList((7, "livemap-compat", false, 900))
            : Redirect(location));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadCompatArtifactAsync(77, Ct));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadCompatArtifact_WhenTheDownloadDoesNotRedirect_IsAnError()
    {
        var (client, _, _) = Create(request => request.Uri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal)
            ? ArtifactList((7, "livemap-compat", false, 900))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipOf("report.json")) });

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadCompatArtifactAsync(77, Ct));
    }

    [Fact]
    public async Task ReadCompatArtifact_WhenTheListedSizeIsOverTheCap_DownloadsNothing()
    {
        var (client, handler, _) = Create(ArtifactFlow(ZipOf("report.json"), (7, "livemap-compat", false, LivemapGitHub.MaxArtifactBytes + 1)));

        await Assert.ThrowsAsync<CompatArtifactException>(() => client.ReadCompatArtifactAsync(77, Ct));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReadCompatArtifact_StopsReadingADownloadThatNeverEnds()
    {
        var (client, _, _) = Create(request => request.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("/artifacts", StringComparison.Ordinal) => ArtifactList((7, "livemap-compat", false, 900)),
            var path when path.EndsWith("/zip", StringComparison.Ordinal) => Redirect("https://blob.example/store/blob?sig=abc"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) },
        });

        await Assert.ThrowsAsync<CompatArtifactException>(() => client.ReadCompatArtifactAsync(77, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task ReadBuildsJson_ReadsTheForkFileAtTheCommit_WithContentsReadOnly()
    {
        var (client, handler, tokens) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"builds":[]}""") });

        var text = await client.ReadBuildsJsonAsync(Sha, Ct);

        Assert.Equal("""{"builds":[]}""", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/contents/data/builds.json", request.Uri.AbsolutePath);
        Assert.Contains($"ref={Sha}", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ReadBuildsJson_WhenTheFileIsMissing_IsNull()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.ReadBuildsJsonAsync(Sha, Ct));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("0123456789abcdef0123456789abcdef01234567&path=x")]
    [InlineData("0123456789ABCDEF0123456789abcdef01234567")]
    [InlineData("")]
    public async Task ReadBuildsJson_RefusesAnythingButACommitSha(string sha)
    {
        var (client, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ReadBuildsJsonAsync(sha, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadBuildsJson_WhenTheFileIsOverTheCap_IsRefused()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', CompatArtifactZip.MaxFileBytes + 5)) });

        await Assert.ThrowsAsync<CompatArtifactException>(() => client.ReadBuildsJsonAsync(Sha, Ct));
    }

    [Fact]
    public async Task ReadBuildsSchema_ReadsTheForkSchemaAtTheCommit_WithContentsReadOnly()
    {
        var (client, handler, tokens) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"type":"object"}""") });

        var text = await client.ReadBuildsSchemaAsync(Sha, Ct);

        Assert.Equal("""{"type":"object"}""", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/contents/data/builds.schema.json", request.Uri.AbsolutePath);
        Assert.Contains($"ref={Sha}", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ReadBuildsSchema_WhenTheFileIsMissing_IsNull()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.ReadBuildsSchemaAsync(Sha, Ct));
    }

    [Fact]
    public async Task ReadBuildsSchema_RefusesAnythingButACommitSha()
    {
        var (client, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ReadBuildsSchemaAsync("main", Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadBuildsSchema_WhenTheFileIsOverTheCap_IsRefused()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', CompatArtifactZip.MaxFileBytes + 5)) });

        await Assert.ThrowsAsync<CompatArtifactException>(() => client.ReadBuildsSchemaAsync(Sha, Ct));
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record MintedToken(string Repo, IReadOnlyDictionary<string, string> Permissions);

    private sealed class RecordingTokens : IGitHubInstallationTokenProvider
    {
        private readonly List<MintedToken> _minted = [];

        public IReadOnlyList<MintedToken> Minted => _minted;

        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
        {
            _minted.Add(new MintedToken(repo, permissions));
            return Task.FromResult("ghs_install_token");
        }
    }
}
