using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPullRequestsTests
{
    private const string Fork = "octo/livemap";

    private static readonly string Oid = new('a', 40);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class RecordingTokens : IGitHubInstallationTokenProvider
    {
        public List<(string Repo, Dictionary<string, string> Permissions)> Minted { get; } = [];

        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
        {
            Minted.Add((repo, permissions.ToDictionary(p => p.Key, p => p.Value)));
            return Task.FromResult("ghs_install_token");
        }
    }

    internal sealed class FixedJwt : IGitHubAppJwtSource
    {
        public string CreateJwt() => "app.jwt.value";
    }

    private static (LivemapPullRequests Client, StubHttpHandler Handler, RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.example/") };
        var tokens = new RecordingTokens();
        return (new LivemapPullRequests(http, tokens, new FixedJwt(), Options.Create(new LivemapOptions { Enabled = true, Repo = Fork })), handler, tokens);
    }

    private static Dictionary<string, string> Perms(params string[] pairs)
    {
        var result = new Dictionary<string, string>();
        for (var i = 0; i < pairs.Length; i += 2)
        {
            result[pairs[i]] = pairs[i + 1];
        }

        return result;
    }

    // The pull request list leaves out the fields GitHub counts for a single pull request, such as commits and merged.
    private static string PullJson(int number = 7, string? headRepo = Fork, bool listed = false)
    {
        var repo = headRepo is null ? "null" : $$"""{ "full_name": "{{headRepo}}" }""";
        var counted = listed ? string.Empty : """ "merged": false, "commits": 1,""";
        return $$"""
            {
              "number": {{number}}, "state": "open", "draft": false,{{counted}}
              "user": { "login": "skynet-rw[bot]", "type": "Bot" },
              "base": { "ref": "main", "repo": { "full_name": "{{Fork}}" } },
              "head": { "ref": "livemap/build-400", "sha": "{{Oid}}", "repo": {{repo}} }
            }
            """;
    }

    [Fact]
    public async Task TheMainHead_IsReadFromTheRefOfTheFork_WithAContentsReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json($$"""{"object":{"sha":"{{Oid}}","type":"commit"} }"""));

        var oid = await client.GetMainHeadAsync(Ct);

        Assert.Equal(Oid, oid);
        Assert.Equal($"/repos/{Fork}/git/ref/heads/main", Assert.Single(handler.Requests).Uri.AbsolutePath);
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(Perms("contents", "read"), minted.Permissions);
    }

    [Fact]
    public async Task ABranch_IsCreatedAtTheGivenOid_WithAContentsWriteToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.Created));

        await client.CreateBranchAsync("livemap/build-400", Oid, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/repos/{Fork}/git/refs", request.Uri.AbsolutePath);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("refs/heads/livemap/build-400", (string?)body["ref"]);
        Assert.Equal(Oid, (string?)body["sha"]);
        Assert.Equal(Perms("contents", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("livemap/build-")]
    [InlineData("livemap/build-4x")]
    [InlineData("livemap/build-400/../main")]
    public async Task OnlyABuildBranch_CanBeCreatedCommittedToOrDeleted(string branch)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateBranchAsync(branch, Oid, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFileAsync(branch, Oid, "fix(builds): add server build 1", "data/builds.json", "{}", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteBranchAsync(branch, Ct));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/data/builds.json")]
    [InlineData("data/../builds.json")]
    [InlineData("data//builds.json")]
    [InlineData("data/builds.json?x=1")]
    public async Task OnlyAPlainRelativePath_CanBeCommittedOrLookedUp(string path)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFileAsync("livemap/build-400", Oid, "fix(builds): add server build 400", path, "{}", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileModeAsync(Oid, path, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EveryPathACodeResultMayCarry_IsOneTheCommitAccepts()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"data":{"createCommitOnBranch":{"commit":{"oid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}}"""));

        var accepted = 0;
        for (var c = '\0'; c < '\u0300'; c++)
        {
            var path = $"src/a{c}b.cpp";
            if (LivemapResult.CheckFiles([new LivemapFile(path, "x")]) is null)
            {
                accepted++;
                var oid = await client.CommitFilesAsync("livemap/build-400", Oid, "fix(builds): support server build 400", [new LivemapFile(path, "x")], Ct);
                Assert.Equal(new string('b', 40), oid);
            }
        }

        Assert.True(accepted > 0, "no path was accepted, so nothing was checked");
    }

    [Fact]
    public async Task TheCommit_GoesThroughGraphQlWithTheExpectedHead_AndCarriesTheFileAsItIs()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"data":{"createCommitOnBranch":{"commit":{"oid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}}"""));

        var oid = await client.CommitFileAsync("livemap/build-400", Oid, "fix(builds): add server build 400", "data/builds.json", "{\"a\":1}\n", Ct);

        Assert.Equal(new string('b', 40), oid);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/graphql", request.Uri.AbsolutePath);
        var input = JsonNode.Parse(request.Body!)!["variables"]!["input"]!;
        Assert.Equal(Fork, (string?)input["branch"]!["repositoryNameWithOwner"]);
        Assert.Equal("livemap/build-400", (string?)input["branch"]!["branchName"]);
        Assert.Equal(Oid, (string?)input["expectedHeadOid"]);
        Assert.Equal("fix(builds): add server build 400", (string?)input["message"]!["headline"]);
        var file = Assert.Single(input["fileChanges"]!["additions"]!.AsArray())!;
        Assert.Equal("data/builds.json", (string?)file["path"]);
        Assert.Equal("{\"a\":1}\n", Encoding.UTF8.GetString(Convert.FromBase64String((string)file["contents"]!)));
        Assert.Null(input["fileChanges"]!["deletions"]);
        Assert.Equal(Perms("contents", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task SeveralFiles_GoInOneCommit_EachAsItIs()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"data":{"createCommitOnBranch":{"commit":{"oid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}}"""));
        LivemapFile[] files = [new("data/builds.json", "{\"a\":1}\n"), new("src/scan.cpp", "int x;\n// é\n")];

        var oid = await client.CommitFilesAsync("livemap/build-400", Oid, "fix(builds): support server build 400", files, Ct);

        Assert.Equal(new string('b', 40), oid);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/graphql", request.Uri.AbsolutePath);
        var input = JsonNode.Parse(request.Body!)!["variables"]!["input"]!;
        Assert.Equal(Oid, (string?)input["expectedHeadOid"]);
        Assert.Equal("fix(builds): support server build 400", (string?)input["message"]!["headline"]);
        var additions = input["fileChanges"]!["additions"]!.AsArray();
        Assert.Equal(["data/builds.json", "src/scan.cpp"], additions.Select(a => (string?)a!["path"]));
        Assert.Equal(files.Select(f => f.Content), additions.Select(a => Encoding.UTF8.GetString(Convert.FromBase64String((string)a!["contents"]!))));
        Assert.Null(input["fileChanges"]!["deletions"]);
        Assert.Equal(Perms("contents", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ACommitNeedsFiles_AndEachPathOnce()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFilesAsync("livemap/build-400", Oid, "fix(builds): support server build 400", [], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFilesAsync("livemap/build-400", Oid, "fix(builds): support server build 400", [new("src/a.cpp", "1"), new("src/a.cpp", "2")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFilesAsync("livemap/build-400", Oid, "fix(builds): support server build 400", [new("src/a.cpp", "1"), new("src/../b.cpp", "2")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFilesAsync("main", Oid, "fix(builds): support server build 400", [new("src/a.cpp", "1")], Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ACommitThatGraphQlRefuses_IsAnError_EvenOnAn200()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"errors":[{"message":"Expected branch to point to abc"}],"data":{"createCommitOnBranch":null}}"""));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.CommitFileAsync("livemap/build-400", Oid, "fix(builds): add server build 400", "data/builds.json", "{}", Ct));

        Assert.Contains("Expected branch to point to abc", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APullRequest_IsOpenedAgainstMain_FromTheBranch()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"number":42}""", HttpStatusCode.Created));

        var number = await client.OpenAsync("livemap/build-400", "fix(builds): add server build 400", "Compat run: x", Ct);

        Assert.Equal(42, number);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/pulls", request.Uri.AbsolutePath);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("main", (string?)body["base"]);
        Assert.Equal("livemap/build-400", (string?)body["head"]);
        Assert.Equal("fix(builds): add server build 400", (string?)body["title"]);
        Assert.Equal(Perms("pull_requests", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task APullRequest_IsReadWithItsRepositoriesAuthorAndHead()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json(PullJson()));

        var pull = await client.GetAsync(7, Ct);

        Assert.Equal(new LivemapPullRequest(7, "open", false, false, "main", Fork, "livemap/build-400", Fork, Oid, "skynet-rw[bot]", "Bot", 1), pull);
        Assert.Equal($"/repos/{Fork}/pulls/7", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(Perms("pull_requests", "read"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task APullRequestWhoseHeadRepositoryIsGone_ReadsWithAnEmptyHeadRepo()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(PullJson(headRepo: null)));

        Assert.Equal(string.Empty, (await client.GetAsync(7, Ct))!.HeadRepo);
    }

    [Fact]
    public async Task AMissingPullRequest_ReadsAsNull()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Null(await client.GetAsync(7, Ct));
    }

    [Fact]
    public async Task TheOpenPullRequestsOfABranch_AreFoundByTheForkOwnedHead()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json($"[{PullJson(9, listed: true)}]"));

        var found = await client.FindOpenByBranchAsync("livemap/build-400", Ct);

        Assert.Equal(9, Assert.Single(found).Number);
        var query = Assert.Single(handler.Requests).Uri.Query;
        Assert.Contains("head=octo%3Alivemap%2Fbuild-400", query, StringComparison.Ordinal);
        Assert.Contains("state=open", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APullRequestFromAnotherRepository_IsNotOneOfTheBranchesOpenPullRequests()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json($"[{PullJson(9, headRepo: "octo/other", listed: true)},{PullJson(10, listed: true)}]"));

        Assert.Equal(10, Assert.Single(await client.FindOpenByBranchAsync("livemap/build-400", Ct)).Number);
    }

    [Fact]
    public async Task TheFilesOfAPullRequest_ListTheirStatusAndAnyPreviousName()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""[{"filename":"data/builds.json","status":"modified"},{"filename":"b","status":"renamed","previous_filename":"a"}]"""));

        var files = await client.ListFilesAsync(7, Ct);

        Assert.Equal([new("data/builds.json", "modified", null), new("b", "renamed", "a")], files);
        Assert.Equal($"/repos/{Fork}/pulls/7/files", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task TheParentsOfACommit_AreItsParentShas()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json($$"""{"parents":[{"sha":"{{Oid}}"}]}"""));

        Assert.Equal([Oid], await client.GetParentsAsync(new string('c', 40), Ct));
        Assert.Equal($"/repos/{Fork}/git/commits/{new string('c', 40)}", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task ACommit_ReadsItsParentsAndItsFilesWithTheirPatches_FromOneRead()
    {
        var sha = new string('c', 40);
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json($$"""
            {"sha":"{{sha}}","parents":[{"sha":"{{Oid}}"}],"files":[{"filename":"version.txt","status":"modified","patch":"@@ -1 +1 @@"},{"filename":"b","status":"renamed","previous_filename":"a"}]}
            """));

        var commit = await client.GetCommitAsync(sha, Ct);

        Assert.Equal([Oid], commit.Parents);
        Assert.Equal([new("version.txt", "modified", null, "@@ -1 +1 @@"), new("b", "renamed", "a")], commit.Files);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{Fork}/commits/{sha}", request.Uri.AbsolutePath);
        Assert.Contains("per_page=100", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(Perms("contents", "read"), Assert.Single(tokens.Minted).Permissions);
    }

    [Fact]
    public async Task ACommitWithoutParentsOrFiles_ReadsAsEmpty()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}"));

        var commit = await client.GetCommitAsync(new string('c', 40), Ct);

        Assert.Empty(commit.Parents);
        Assert.Empty(commit.Files);
    }

    [Fact]
    public async Task ACommitThatIsNotAHexSha_IsNeverRead()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCommitAsync("main", Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheModeOfAFile_IsReadFromTheTreeOfItsDirectoryAtTheCommit()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"tree":[{"path":"builds.schema.json","mode":"100644","type":"blob"},{"path":"builds.json","mode":"100755","type":"blob"}]}"""));

        var mode = await client.GetFileModeAsync(Oid, "data/builds.json", Ct);

        Assert.Equal("100755", mode);
        Assert.Equal($"/repos/{Fork}/git/trees/{Oid}:data", Uri.UnescapeDataString(Assert.Single(handler.Requests).Uri.AbsolutePath));
    }

    [Fact]
    public async Task AFileThatIsNotInItsTree_HasNoMode()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"tree":[{"path":"other.json","mode":"100644","type":"blob"}]}"""));

        Assert.Null(await client.GetFileModeAsync(Oid, "data/builds.json", Ct));
    }

    [Fact]
    public async Task TheCheckRuns_AreTheLatestOnesOfTheSha_WithEveryPageTheirAppAndTheirCheckSuite()
    {
        var calls = 0;
        var (client, handler, tokens) = Create(request =>
        {
            calls++;
            return request.Uri.Query.Contains("page=2", StringComparison.Ordinal)
                ? StubHttpHandler.Json("""{"total_count":101,"check_runs":[{"name":"last","status":"completed","conclusion":"success","app":{"slug":"github-actions"},"check_suite":{"id":5001}}]}""")
                : StubHttpHandler.Json($$"""{"total_count":101,"check_runs":[{{string.Join(',', Enumerable.Range(0, 100).Select(i => $$"""{"name":"n{{i}}","status":"completed","conclusion":"success","app":null,"check_suite":null}"""))}}]}""");
        });

        var runs = await client.ListCheckRunsAsync(Oid, Ct);

        Assert.Equal(101, runs.Count);
        Assert.Equal(new LivemapCheckRun("last", "completed", "success", "github-actions", 5001), runs[^1]);
        Assert.Null(runs[0].AppSlug);
        Assert.Equal(0, runs[0].CheckSuiteId);
        Assert.Equal(2, calls);
        Assert.All(handler.Requests, r => Assert.Contains("filter=latest", r.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(Perms("checks", "read"), tokens.Minted.First().Permissions);
    }

    [Fact]
    public async Task TheMerge_IsASquashOfTheGivenHead_UnderTheGivenTitle()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"merged":true,"sha":"dddddddddddddddddddddddddddddddddddddddd"}"""));

        var result = await client.MergeAsync(7, Oid, "fix(builds): add server build 400 (#7)", Ct);

        Assert.Equal(LivemapMergeResult.Merged, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/repos/{Fork}/pulls/7/merge", request.Uri.AbsolutePath);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("squash", (string?)body["merge_method"]);
        Assert.Equal(Oid, (string?)body["sha"]);
        Assert.Equal("fix(builds): add server build 400 (#7)", (string?)body["commit_title"]);
        Assert.Equal(Perms("contents", "write", "pull_requests", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task AMergeGitHubRefuses_IsReportedAsRefused(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Head branch was modified"}""", status));

        Assert.Equal(LivemapMergeResult.Refused, await client.MergeAsync(7, Oid, "t (#7)", Ct));
    }

    [Fact]
    public async Task AMergeGitHubAnswersNotAllowed_IsReportedAsNotMergeable()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Pull Request is not mergeable"}""", HttpStatusCode.MethodNotAllowed));

        Assert.Equal(LivemapMergeResult.NotMergeable, await client.MergeAsync(7, Oid, "t (#7)", Ct));
    }

    [Fact]
    public async Task AMergeThatFailsOtherwise_IsAnError()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.MergeAsync(7, Oid, "t (#7)", Ct));
    }

    [Fact]
    public async Task APullRequest_IsClosedWithoutMerging()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("{}"));

        await client.ClosePullRequestAsync(7, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/repos/{Fork}/pulls/7", request.Uri.AbsolutePath);
        Assert.Equal("closed", (string?)JsonNode.Parse(request.Body!)!["state"]);
        Assert.Equal(Perms("pull_requests", "write"), Assert.Single(tokens.Minted).Permissions);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task APullRequest_ThatCannotBeClosedAnyMore_IsDone(HttpStatusCode status)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}", status));

        await client.ClosePullRequestAsync(7, Ct);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task APullRequestCloseThatFailsOtherwise_IsAnError()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ClosePullRequestAsync(7, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ABranch_IsDeleted_AndAMissingOneIsFine(HttpStatusCode status)
    {
        var (client, handler, _) = Create(_ => new HttpResponseMessage(status));

        await client.DeleteBranchAsync("livemap/build-400", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/repos/{Fork}/git/refs/heads/livemap/build-400", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task TheAppLogin_IsTheSlugOfTheAppThatTheJwtNames_WithTheBotSuffix()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"slug":"skynet-rw"}"""));

        Assert.Equal("skynet-rw[bot]", await client.GetAppLoginAsync(Ct));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/app", request.Uri.AbsolutePath);
        Assert.Equal("Bearer app.jwt.value", request.Authorization);
        Assert.Empty(tokens.Minted);
    }
}
