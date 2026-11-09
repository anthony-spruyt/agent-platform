using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapReleasesTests
{
    private const string Fork = "octo/livemap";

    private const string Tag = "v1.10.0";

    private const string SlsaType = "https://slsa.dev/provenance/v1";

    private static readonly string Hex = new('c', 64);

    private static readonly string Commit = new('d', 40);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LivemapReleases Client, StubHttpHandler Handler, LivemapPullRequestsTests.RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.example/") };
        var tokens = new LivemapPullRequestsTests.RecordingTokens();
        return (new LivemapReleases(http, tokens, Options.Create(new LivemapOptions { Enabled = true, Repo = Fork })), handler, tokens);
    }

    private static Dictionary<string, string> Perms(string key, string level) => new() { [key] = level };

    private static string ReleaseJson(string tag = Tag, bool draft = false, bool immutable = true) => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": false, "immutable": {{(immutable ? "true" : "false")}},
          "html_url": "https://github.example/{{Fork}}/releases/tag/{{tag}}",
          "assets": [
            { "id": 11, "name": "EnshroudedLiveMap-1.10.0.zip", "size": 9000, "digest": "sha256:{{Hex}}", "state": "uploaded" },
            { "id": 12, "name": "EnshroudedLiveMap-1.10.0.zip.sha256", "size": 94, "digest": null, "state": "uploaded" }
          ]
        }
        """;

    [Fact]
    public async Task TheLatestRelease_IsReadWithItsAssets_WithAContentsReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json(ReleaseJson()));

        var release = await client.GetLatestAsync(Ct);

        Assert.Equal($"/repos/{Fork}/releases/latest", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(Tag, release!.Tag);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
        Assert.True(release.Immutable);
        Assert.Equal($"https://github.example/{Fork}/releases/tag/{Tag}", release.Url);
        Assert.Collection(
            release.Assets,
            zip =>
            {
                Assert.Equal(11, zip.Id);
                Assert.Equal("EnshroudedLiveMap-1.10.0.zip", zip.Name);
                Assert.Equal(9000, zip.Size);
                Assert.Equal($"sha256:{Hex}", zip.Digest);
            },
            sum => Assert.Null(sum.Digest));
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(Perms("contents", "read"), minted.Permissions);
    }

    [Fact]
    public async Task ARepositoryWithoutAPublishedRelease_HasNoLatestRelease()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await client.GetLatestAsync(Ct));
    }

    [Fact]
    public async Task AReleaseIsReadByItsTag()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json(ReleaseJson()));

        var release = await client.GetByTagAsync(Tag, Ct);

        Assert.Equal($"/repos/{Fork}/releases/tags/{Tag}", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(Tag, release!.Tag);
    }

    [Fact]
    public async Task AReleaseThatIsNotPublishedYet_IsNotFoundByItsTag()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await client.GetByTagAsync(Tag, Ct));
    }

    [Theory]
    [InlineData("1.10.0")]
    [InlineData("v1.10")]
    [InlineData("v1.10.0-rc1")]
    [InlineData("v1.10.0/../latest")]
    [InlineData("main")]
    [InlineData("")]
    public async Task OnlyAPlainReleaseTag_IsLookedUp(string tag)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetByTagAsync(tag, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTagCommitAsync(tag, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTagRunAsync(tag, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheCommitOfATag_IsReadAsTheTagAndNotAsABranchOfTheSameName()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json($$"""{"sha":"{{Commit}}"}"""));

        var sha = await client.GetTagCommitAsync(Tag, Ct);

        Assert.Equal(Commit, sha);
        Assert.Equal($"/repos/{Fork}/commits/tags/{Tag}", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(Perms("contents", "read"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ATagThatDoesNotExist_HasNoCommit()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await client.GetTagCommitAsync(Tag, Ct));
    }

    [Theory]
    [InlineData("""{"sha":"not-a-sha"}""")]
    [InlineData("""{}""")]
    public async Task ACommitAnswerWithoutAShaIsAnError(string body)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(body));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetTagCommitAsync(Tag, Ct));
    }

    [Fact]
    public async Task TheRunOfTheTagPush_IsTheNewestRunOfTheReleaseWorkflowForThatTag()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json($$"""
            {"total_count":1,"workflow_runs":[{"id":4242,"status":"completed","conclusion":"failure","run_attempt":2,"head_sha":"{{Commit}}","html_url":"https://github.example/run/4242"}]}
            """));

        var run = await client.GetTagRunAsync(Tag, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/actions/workflows/release.yaml/runs", request.Uri.AbsolutePath);
        Assert.Contains($"branch={Tag}", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("event=push", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(4242, run!.Id);
        Assert.Equal("completed", run.Status);
        Assert.Equal("failure", run.Conclusion);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(Commit, run.HeadSha);
        Assert.Equal("https://github.example/run/4242", run.Url);
        Assert.Equal(Perms("actions", "read"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ATagThatHasNotStartedItsRun_HasNoRunYet()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"total_count":0,"workflow_runs":[]}"""));

        Assert.Null(await client.GetTagRunAsync(Tag, Ct));
    }

    [Fact]
    public async Task TheFailedJobsOfARun_AreRerunWithAnActionsWriteToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.Created));

        await client.RerunFailedJobsAsync(4242, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/repos/{Fork}/actions/runs/4242/rerun-failed-jobs", request.Uri.AbsolutePath);
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(Perms("actions", "write"), minted.Permissions);
    }

    [Fact]
    public async Task ARerunThatGitHubRefuses_IsAnError()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.RerunFailedJobsAsync(4242, Ct));
    }

    [Fact]
    public async Task AnAssetIsReadAsText_FromTheRedirectTarget_WithoutTheToken()
    {
        var (client, handler, tokens) = Create(AssetFlow("1111  EnshroudedLiveMap-1.10.0.zip\n"));

        var text = await client.ReadAssetTextAsync(12, 1024, Ct);

        Assert.Equal("1111  EnshroudedLiveMap-1.10.0.zip\n", text);
        Assert.Equal($"/repos/{Fork}/releases/assets/12", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("application/octet-stream", handler.Requests[0].Accept);
        Assert.Equal("Bearer ghs_install_token", handler.Requests[0].Authorization);
        Assert.Equal("blob.example", handler.Requests[1].Uri.Host);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Equal(Perms("contents", "read"), Assert.Single(tokens.Minted).Permissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAssetThatIsGone_ReadsAsNull(bool atTheRedirectTarget)
    {
        var (client, _, _) = Create(request => request.Uri.Host == "blob.example" || !atTheRedirectTarget
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Redirect("https://blob.example/store/blob?sig=abc"));

        Assert.Null(await client.ReadAssetTextAsync(12, 1024, Ct));
    }

    [Theory]
    [InlineData("http://blob.example/store/blob")]
    [InlineData("/relative/blob")]
    public async Task AnAssetRedirectOffHttps_IsRefused(string location)
    {
        var (client, handler, _) = Create(_ => Redirect(location));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadAssetTextAsync(12, 1024, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnAssetAnswerWithoutARedirect_IsAnError()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("text") });

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadAssetTextAsync(12, 1024, Ct));
    }

    [Fact]
    public async Task AnAssetOverTheCap_IsAnError()
    {
        var (client, _, _) = Create(AssetFlow(new string('x', 500)));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadAssetTextAsync(12, 100, Ct));
    }

    [Fact]
    public async Task AnAssetAtTheCap_IsRead()
    {
        var (client, _, _) = Create(AssetFlow(new string('x', 100)));

        Assert.Equal(100, (await client.ReadAssetTextAsync(12, 100, Ct))!.Length);
    }

    [Fact]
    public async Task AnAssetIdMustBePositive()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadAssetTextAsync(0, 100, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RerunFailedJobsAsync(0, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheProvenanceOfADigest_IsReadWithoutAnyToken_AndTheSlsaStatementIsParsed()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json(Attestations(Statement(SlsaType), Statement("https://in-toto.io/attestation/release/v0.2"))));

        var found = await client.GetProvenanceAsync(Hex, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/attestations/sha256:{Hex}", request.Uri.AbsolutePath);
        Assert.Null(request.Authorization);
        Assert.Empty(tokens.Minted);
        var provenance = Assert.Single(found);
        Assert.Equal(".github/workflows/release.yaml", provenance.WorkflowPath);
        Assert.Equal($"refs/tags/{Tag}", provenance.WorkflowRef);
        Assert.Equal($"https://github.com/{Fork}", provenance.Repository);
        Assert.Equal(Commit, provenance.CommitSha);
        Assert.Equal([new LivemapProvenanceSubject("EnshroudedLiveMap-1.10.0.zip", Hex)], provenance.Subjects);
    }

    [Fact]
    public async Task AnAttestationWithAnUnreadablePayload_IsLeftOut()
    {
        var broken = """{"bundle":{"dsseEnvelope":{"payload":"not base64!","payloadType":"application/vnd.in-toto+json"}}}""";
        var empty = """{"bundle":{}}""";
        var (client, _, _) = Create(_ => StubHttpHandler.Json($$"""{"attestations":[{{broken}},{{empty}},{{Attestation(Statement(SlsaType))}}]}"""));

        Assert.Single(await client.GetProvenanceAsync(Hex, Ct));
    }

    [Fact]
    public async Task ADigestWithoutAttestations_HasNoProvenance()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Empty(await client.GetProvenanceAsync(Hex, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("sha256:abc")]
    public async Task OnlyAHexDigest_IsLookedUp(string digest)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetProvenanceAsync(digest, Ct));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static Func<RecordedRequest, HttpResponseMessage> AssetFlow(string content) => request =>
        request.Uri.Host == "blob.example"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
            : Redirect("https://blob.example/store/blob?sig=abc");

    private static string Statement(string predicateType) => $$"""
        {
          "_type": "https://in-toto.io/Statement/v1",
          "subject": [
            { "uri": "pkg:github/{{Fork}}@{{Tag}}", "digest": { "sha1": "{{Commit}}" } },
            { "name": "EnshroudedLiveMap-1.10.0.zip", "digest": { "sha256": "{{Hex}}" } }
          ],
          "predicateType": "{{predicateType}}",
          "predicate": {
            "buildDefinition": {
              "externalParameters": { "workflow": { "ref": "refs/tags/{{Tag}}", "repository": "https://github.com/{{Fork}}", "path": ".github/workflows/release.yaml" } },
              "resolvedDependencies": [ { "uri": "git+https://github.com/{{Fork}}@refs/tags/{{Tag}}", "digest": { "gitCommit": "{{Commit}}" } } ]
            }
          }
        }
        """;

    private static string Attestation(string statementJson) =>
        new JsonObject
        {
            ["bundle"] = new JsonObject
            {
                ["dsseEnvelope"] = new JsonObject
                {
                    ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(statementJson)),
                    ["payloadType"] = "application/vnd.in-toto+json",
                },
            },
        }.ToJsonString();

    private static string Attestations(params string[] statements) => $$"""{"attestations":[{{string.Join(',', statements.Select(Attestation))}}]}""";
}
