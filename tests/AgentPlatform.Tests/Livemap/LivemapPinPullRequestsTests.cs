using System.Net;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPinPullRequestsTests
{
    private const string Fork = "octo/livemap";

    private const string PinRepo = "octo/pins";

    private const string PinPath = "apps/pin.yaml";

    private const string ReleaseBranch = "release-please--branches--main";

    private static readonly string Oid = new('a', 40);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LivemapPullRequests Client, StubHttpHandler Handler, LivemapPullRequestsTests.RecordingTokens Tokens) Create(LivemapRepoKind kind, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.example/") };
        var tokens = new LivemapPullRequestsTests.RecordingTokens();
        var options = Options.Create(new LivemapOptions { Enabled = true, Repo = Fork, PinRepo = PinRepo, PinPath = PinPath });
        return (new LivemapPullRequests(http, tokens, new LivemapPullRequestsTests.FixedJwt(), options, kind), handler, tokens);
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

    [Fact]
    public async Task ThePinClient_ReadsMainOfThePinRepo_WithATokenForThatRepoOnly()
    {
        var (client, handler, tokens) = Create(LivemapRepoKind.Pin, _ => StubHttpHandler.Json($$"""{"object":{"sha":"{{Oid}}","type":"commit"} }"""));

        Assert.Equal(Oid, await client.GetMainHeadAsync(Ct));

        Assert.Equal($"/repos/{PinRepo}/git/ref/heads/main", Assert.Single(handler.Requests).Uri.AbsolutePath);
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(PinRepo, minted.Repo);
        Assert.Equal(Perms("contents", "read"), minted.Permissions);
    }

    [Fact]
    public async Task ThePinPullRequest_IsOpenedAndSquashMerged_InThePinRepo()
    {
        var (client, handler, tokens) = Create(LivemapRepoKind.Pin, request => request.Method == HttpMethod.Post
            ? StubHttpHandler.Json("""{"number":9}""", HttpStatusCode.Created)
            : StubHttpHandler.Json("""{"merged":true}"""));

        var number = await client.OpenAsync("livemap/pin-v1.10.0", "chore(enshrouded): update livemap to v1.10.0", "body", Ct);
        var result = await client.MergeAsync(number, Oid, "chore(enshrouded): update livemap to v1.10.0 (#9)", Ct);

        Assert.Equal(LivemapMergeResult.Merged, result);
        Assert.Equal([$"/repos/{PinRepo}/pulls", $"/repos/{PinRepo}/pulls/9/merge"], handler.Requests.Select(r => r.Uri.AbsolutePath));
        var merge = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("squash", (string?)merge["merge_method"]);
        Assert.Equal(Oid, (string?)merge["sha"]);
        Assert.All(tokens.Minted, minted => Assert.Equal(PinRepo, minted.Repo));
        Assert.Equal(Perms("pull_requests", "write"), tokens.Minted[0].Permissions);
        Assert.Equal(Perms("contents", "write", "pull_requests", "write"), tokens.Minted[1].Permissions);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("livemap/build-400")]
    [InlineData("livemap/pin-1.10.0")]
    [InlineData("livemap/pin-v1.10")]
    [InlineData("livemap/pin-v1.10.0-rc1")]
    [InlineData("livemap/pin-v1.10.0/../main")]
    [InlineData(ReleaseBranch)]
    public async Task OnlyAPinBranch_CanBeCreatedCommittedToOrDeletedInThePinRepo(string branch)
    {
        var (client, handler, _) = Create(LivemapRepoKind.Pin, _ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateBranchAsync(branch, Oid, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFileAsync(branch, Oid, "chore(enshrouded): update livemap to v1.10.0", PinPath, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteBranchAsync(branch, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenAsync(branch, "title", "body", Ct));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("livemap/pin-v1.10.0")]
    [InlineData(ReleaseBranch)]
    public async Task TheForkClient_NeverWritesToAPinBranchOrTheReleaseBranch(string branch)
    {
        var (client, handler, _) = Create(LivemapRepoKind.Fork, _ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateBranchAsync(branch, Oid, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFileAsync(branch, Oid, "fix(builds): add server build 1", "data/builds.json", "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteBranchAsync(branch, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenAsync(branch, "title", "body", Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheForkClient_FindsTheOpenPullRequestOfTheReleaseBranch()
    {
        var (client, handler, _) = Create(LivemapRepoKind.Fork, _ => StubHttpHandler.Json("[]"));

        Assert.Empty(await client.FindOpenByBranchAsync(ReleaseBranch, Ct));

        Assert.Equal($"/repos/{Fork}/pulls", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Contains($"head=octo%3A{ReleaseBranch}", Assert.Single(handler.Requests).Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LivemapRepoKind.Fork, "main")]
    [InlineData(LivemapRepoKind.Fork, "livemap/pin-v1.10.0")]
    [InlineData(LivemapRepoKind.Pin, ReleaseBranch)]
    [InlineData(LivemapRepoKind.Pin, "livemap/build-400")]
    public async Task OnlyTheBranchesOfTheRepo_AreLookedUpForAnOpenPullRequest(LivemapRepoKind kind, string branch)
    {
        var (client, handler, _) = Create(kind, _ => StubHttpHandler.Json("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.FindOpenByBranchAsync(branch, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ThePinFile_IsReadByItsExactPath_AtTheGivenCommit_AsRawText()
    {
        var (client, handler, tokens) = Create(LivemapRepoKind.Pin, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("LIVEMAP_VERSION: \"v1.9.1\"\n") });

        var text = await client.ReadFileAsync(Oid, PinPath, Ct);

        Assert.Equal("LIVEMAP_VERSION: \"v1.9.1\"\n", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/{PinRepo}/contents/{PinPath}", request.Uri.AbsolutePath);
        Assert.Equal($"?ref={Oid}", request.Uri.Query);
        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(PinRepo, minted.Repo);
        Assert.Equal(Perms("contents", "read"), minted.Permissions);
    }

    [Theory]
    [InlineData("apps/other.yaml")]
    [InlineData("apps")]
    [InlineData("apps/")]
    [InlineData("apps/pin.yaml/../secrets.yaml")]
    [InlineData("pin.yaml")]
    [InlineData("")]
    public async Task OnlyThePinFile_CanBeReadOrCommittedInThePinRepo(string path)
    {
        var (client, handler, _) = Create(LivemapRepoKind.Pin, _ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadFileAsync(Oid, path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CommitFileAsync("livemap/pin-v1.10.0", Oid, "chore(enshrouded): update livemap to v1.10.0", path, "x", Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ThePinCommit_GoesToThePinRepo()
    {
        var (client, handler, _) = Create(LivemapRepoKind.Pin, _ => StubHttpHandler.Json("""{"data":{"createCommitOnBranch":{"commit":{"oid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}}"""));

        await client.CommitFileAsync("livemap/pin-v1.10.0", Oid, "chore(enshrouded): update livemap to v1.10.0", PinPath, "x", Ct);

        var input = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["variables"]!["input"]!;
        Assert.Equal(PinRepo, (string?)input["branch"]!["repositoryNameWithOwner"]);
        Assert.Equal(PinPath, (string?)Assert.Single(input["fileChanges"]!["additions"]!.AsArray())!["path"]);
    }

    [Fact]
    public async Task AFileOfTheFork_IsReadAtTheGivenCommit_AndAMissingOneReadsAsNull()
    {
        var (client, handler, tokens) = Create(LivemapRepoKind.Fork, request => request.Uri.AbsolutePath.EndsWith("/version.txt", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1.10.0\n") }
            : StubHttpHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Equal("1.10.0\n", await client.ReadFileAsync(Oid, "version.txt", Ct));
        Assert.Null(await client.ReadFileAsync(Oid, "gone.txt", Ct));

        Assert.All(handler.Requests, request => Assert.StartsWith($"/repos/{Fork}/contents/", request.Uri.AbsolutePath, StringComparison.Ordinal));
        Assert.All(tokens.Minted, minted => Assert.Equal(Fork, minted.Repo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("ABCDEF")]
    public async Task AFile_IsReadOnlyAtACommitSha(string reference)
    {
        var (client, handler, _) = Create(LivemapRepoKind.Fork, _ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadFileAsync(reference, "version.txt", Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFileOverTheSizeCap_IsAnError()
    {
        var (client, _, _) = Create(LivemapRepoKind.Fork, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70_000)) });

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ReadFileAsync(Oid, "version.txt", Ct));
    }

    [Fact]
    public async Task ThePinClient_NeverListsAFolderOrReadsChecks()
    {
        var (client, handler, _) = Create(LivemapRepoKind.Pin, _ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetFileModeAsync(Oid, PinPath, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListCheckRunsAsync(Oid, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task APullRequest_ReadsItsTitle_AndItsFilesTheirPatch()
    {
        var (client, _, _) = Create(LivemapRepoKind.Fork, request => request.Uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)
            ? StubHttpHandler.Json("""[{"filename":"CMakeLists.txt","status":"modified","patch":"@@ -1 +1 @@\n-a\n+b"},{"filename":"big.bin","status":"modified"}]""")
            : StubHttpHandler.Json($$"""
                {"number":7,"state":"open","merged":false,"draft":false,"commits":1,"title":"chore(main): release 1.10.0",
                 "user":{"login":"bot[bot]","type":"Bot"},
                 "base":{"ref":"main","repo":{"full_name":"{{Fork}}" } },
                 "head":{"ref":"{{ReleaseBranch}}","sha":"{{Oid}}","repo":{"full_name":"{{Fork}}" } } }
                """));

        var pull = await client.GetAsync(7, Ct);
        var files = await client.ListFilesAsync(7, Ct);

        Assert.Equal("chore(main): release 1.10.0", pull!.Title);
        Assert.Equal("@@ -1 +1 @@\n-a\n+b", files[0].Patch);
        Assert.Null(files[1].Patch);
    }
}
