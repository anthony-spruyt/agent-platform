using System.Globalization;
using System.Net;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.GitHub;

public class GitHubCheckRunTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const long OwnAppId = 3233443;
    private const string TriageId = "renovate-triage/octo/widgets/7@" + Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingTokens : IGitHubInstallationTokenProvider
    {
        public List<Dictionary<string, string>> Requests { get; } = [];

        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
        {
            Requests.Add(permissions.ToDictionary(p => p.Key, p => p.Value));
            return Task.FromResult("ghs_install_token");
        }
    }

    private static (GitHubApiClient Client, StubHttpHandler Handler, RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var tokens = new RecordingTokens();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubApiClient(http, tokens, Options.Create(new GitHubAppOptions { AppId = OwnAppId.ToString(CultureInfo.InvariantCulture) })), handler, tokens);
    }

    private static string Runs(params object[] runs) => JsonSerializer.Serialize(new { total_count = runs.Length, check_runs = runs });

    private static object Run(long id, string name, long appId, string? externalId, string status = "completed", string? conclusion = "success") =>
        new { id, name, status, conclusion, external_id = externalId, app = new { id = appId, slug = "skynet-rw" } };

    private static GitHubCheckRunWrite Write(string status = "in_progress", string? conclusion = null) =>
        new("Agent triage", Sha, TriageId, status, conclusion, "Agent triage", "Waiting for CI");

    [Fact]
    public async Task ListCheckRuns_ParsesTheAppIdAndTheExternalId()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(Runs(Run(1, "build", 15368, "ci-1"), new { id = 2, name = "scan", status = "queued", app = (object?)null })));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(new GitHubCheckRun(1, "build", "completed", "success", "skynet-rw", 15368, "ci-1"), runs[0]);
        Assert.Equal(new GitHubCheckRun(2, "scan", "queued", null, null, null, null), runs[1]);
    }

    [Fact]
    public async Task ListCheckRuns_DropsOurTriageRunsButKeepsTheSameAppsOtherRuns()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(Runs(
            Run(1, "Agent triage", OwnAppId, TriageId, "in_progress", null),
            Run(2, "Other workflow", OwnAppId, "pr-review/octo/widgets/7@" + Sha),
            Run(3, "No external id", OwnAppId, null),
            Run(4, "Impostor", 15368, TriageId))));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal([2L, 3L, 4L], runs.Select(r => r.Id));
    }

    private const string SuitesJson = """
        {"total_count":2,"check_suites":[
          {"id":1,"status":"completed","conclusion":"success","app":{"id":15368,"slug":"github-actions"},"latest_check_runs_count":1},
          {"id":2,"status":"in_progress","conclusion":null,"app":{"id":3233443,"slug":"skynet-rw"},"latest_check_runs_count":1}]}
        """;

    private static HttpResponseMessage RunsAndSuites(RecordedRequest request, string runs) =>
        StubHttpHandler.Json(request.Uri.AbsolutePath.EndsWith("/check-suites", StringComparison.Ordinal) ? SuitesJson : runs);

    [Fact]
    public async Task OurOwnFailedTriageRun_IsNeverListedAsAFailedCheck()
    {
        var (client, _, _) = Create(r => RunsAndSuites(r, Runs(
            Run(1, "build", 15368, null, conclusion: "failure"),
            Run(2, "Agent triage", OwnAppId, TriageId, conclusion: "failure"))));

        var failed = TriageRules.FailedChecks(await client.ListCheckRunsAsync("octo/widgets", Sha, Ct));

        Assert.Equal(["build"], failed.Select(c => c.Name));
    }

    [Fact]
    public async Task OurOwnOpenRunAndSuite_NeverMakeTheChecksPending()
    {
        var (client, _, _) = Create(r => RunsAndSuites(r, Runs(
            Run(1, "build", 15368, null),
            Run(2, "Agent triage", OwnAppId, TriageId, "in_progress", null))));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);
        var suites = await client.ListCheckSuitesAsync("octo/widgets", Sha, Ct);

        Assert.Equal(CiState.Passed, TriageRules.AssessChecks(runs, suites));
    }

    [Fact]
    public async Task ListCheckSuites_ParsesTheAppIdAndDropsOurOwnSuites()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""
            {"total_count":3,"check_suites":[
              {"id":1,"status":"completed","conclusion":"success","app":{"id":15368,"slug":"github-actions"},"latest_check_runs_count":2},
              {"id":2,"status":"in_progress","conclusion":null,"app":{"id":3233443,"slug":"skynet-rw"},"latest_check_runs_count":1},
              {"id":3,"status":"completed","conclusion":"success","app":null,"latest_check_runs_count":1}]}
            """));

        var suites = await client.ListCheckSuitesAsync("octo/widgets", Sha, Ct);

        Assert.Equal(
            [new GitHubCheckSuite(1, "completed", "success", "github-actions", 2, 15368), new GitHubCheckSuite(3, "completed", "success", null, 1, null)],
            suites);
    }

    [Fact]
    public async Task FindCheckRun_SeesOurOwnRunAndMatchesNameAndExternalId()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json(Runs(
            Run(1, "Agent triage", OwnAppId, "renovate-triage/octo/widgets/7@other", "in_progress", null),
            Run(2, "Different name", OwnAppId, TriageId),
            Run(3, "Agent triage", OwnAppId, TriageId, "in_progress", null))));

        var found = await client.FindCheckRunAsync("octo/widgets", Sha, "Agent triage", TriageId, Ct);

        Assert.Equal(3, found?.Id);
        Assert.Equal("in_progress", found?.Status);
        Assert.All(handler.Requests, r => Assert.Equal($"/repos/octo/widgets/commits/{Sha}/check-runs", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "read" }, tokens.Requests[0]);
    }

    [Fact]
    public async Task FindCheckRun_WithSeveralMatchingRuns_ReturnsTheNewest()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(Runs(
            Run(5, "Agent triage", OwnAppId, TriageId, "completed", "skipped"),
            Run(9, "Agent triage", OwnAppId, TriageId, "in_progress", null),
            Run(7, "Agent triage", OwnAppId, TriageId, "completed", "success"))));

        var found = await client.FindCheckRunAsync("octo/widgets", Sha, "Agent triage", TriageId, Ct);

        Assert.Equal(9, found?.Id);
    }

    [Fact]
    public async Task FindCheckRun_IgnoresAnotherAppsRunWithTheSameExternalId()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(Runs(Run(1, "Agent triage", 15368, TriageId))));

        Assert.Null(await client.FindCheckRunAsync("octo/widgets", Sha, "Agent triage", TriageId, Ct));
    }

    [Fact]
    public async Task CreateCheckRun_PostsTheRunWithAChecksWriteToken_AndReturnsTheId()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":991,"name":"Agent triage"}""", HttpStatusCode.Created));

        var id = await client.CreateCheckRunAsync("octo/widgets", Write(), Ct);

        Assert.Equal(991, id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/octo/widgets/check-runs", request.Uri.AbsolutePath);
        Assert.Equal("Bearer ghs_install_token", request.Authorization);
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "write" }, Assert.Single(tokens.Requests));
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("Agent triage", root.GetProperty("name").GetString());
        Assert.Equal(Sha, root.GetProperty("head_sha").GetString());
        Assert.Equal(TriageId, root.GetProperty("external_id").GetString());
        Assert.Equal("in_progress", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("conclusion", out _));
        Assert.Equal("Agent triage", root.GetProperty("output").GetProperty("title").GetString());
        Assert.Equal("Waiting for CI", root.GetProperty("output").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task CreateCheckRun_OfACompletedRun_CarriesTheConclusion()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"id":991}""", HttpStatusCode.Created));

        await client.CreateCheckRunAsync("octo/widgets", Write("completed", "skipped"), Ct);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("skipped", body.RootElement.GetProperty("conclusion").GetString());
    }

    [Fact]
    public async Task UpdateCheckRun_PatchesOnlyStatusConclusionAndOutput()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":991}"""));

        await client.UpdateCheckRunAsync("octo/widgets", 991, Write("completed", "success"), Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/repos/octo/widgets/check-runs/991", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "write" }, Assert.Single(tokens.Requests));
        using var body = JsonDocument.Parse(request.Body!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["conclusion", "output", "status"], names);
    }

    [Fact]
    public async Task CreateAndUpdate_RejectABadRepoOrId()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCheckRunAsync("not a repo", Write(), Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.UpdateCheckRunAsync("octo/widgets", 0, Write(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCheckRunAsync("octo/widgets", Write() with { HeadSha = "../x" }, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFailedCreate_Throws()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.UnprocessableEntity));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateCheckRunAsync("octo/widgets", Write(), Ct));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failure.StatusCode);
    }
}
