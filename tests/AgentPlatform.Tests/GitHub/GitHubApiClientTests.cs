using System.Net;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.GitHub;

public class GitHubApiClientTests
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
        return (new GitHubApiClient(http, tokens), handler, tokens);
    }

    private static string CheckRunsPage(int from, int count, int total) =>
        JsonSerializer.Serialize(new
        {
            total_count = total,
            check_runs = Enumerable.Range(from, count).Select(i => new
            {
                id = i,
                name = $"check-{i}",
                status = "completed",
                conclusion = i % 2 == 0 ? "success" : "failure",
                head_sha = Sha,
                app = new { slug = "github-actions" },
            }),
        });

    private static HttpResponseMessage Page(RecordedRequest request, int total, Func<int, int, string> render)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        var page = int.Parse(query["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var perPage = int.Parse(query["per_page"] ?? "30", System.Globalization.CultureInfo.InvariantCulture);
        var from = ((page - 1) * perPage) + 1;
        var count = Math.Max(0, Math.Min(perPage, total - from + 1));
        return StubHttpHandler.Json(render(from, count));
    }

    [Fact]
    public async Task GetPullRequest_ParsesTheFieldsThatTheTriageNeeds()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""
            {"number":7,"title":"Update dep","body":"notes","state":"open","draft":false,"merged":false,
             "mergeable_state":"clean","user":{"login":"renovate[bot]","id":29139614},
             "head":{"ref":"renovate/dep","sha":"abc","repo":{"full_name":"octo/widgets"}},
             "base":{"ref":"main"},
             "labels":[{"name":"dependencies"},{"name":"blocked"}]}
            """));

        var pr = await client.GetPullRequestAsync("octo/widgets", 7, Ct);

        Assert.Equal(7, pr.Number);
        Assert.Equal("Update dep", pr.Title);
        Assert.Equal("notes", pr.Body);
        Assert.Equal("open", pr.State);
        Assert.False(pr.Draft);
        Assert.False(pr.Merged);
        Assert.Equal("clean", pr.MergeableState);
        Assert.Equal("renovate[bot]", pr.AuthorLogin);
        Assert.Equal(29139614, pr.AuthorId);
        Assert.Equal("renovate/dep", pr.HeadRef);
        Assert.Equal("abc", pr.HeadSha);
        Assert.Equal("octo/widgets", pr.HeadRepo);
        Assert.Equal("main", pr.BaseRef);
        Assert.Equal(["dependencies", "blocked"], pr.Labels);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/repos/octo/widgets/pulls/7", request.Uri.AbsolutePath);
        Assert.Equal("Bearer ghs_install_token", request.Authorization);
        var (repo, permissions) = Assert.Single(tokens.Requests);
        Assert.Equal("octo/widgets", repo);
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, permissions);
    }

    [Fact]
    public async Task GetPullRequest_ToleratesMissingOptionalFields()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"number":7,"state":"open","head":{"ref":"r","sha":"s","repo":null},"base":{"ref":"main"}}"""));

        var pr = await client.GetPullRequestAsync("octo/widgets", 7, Ct);

        Assert.Null(pr.Body);
        Assert.Null(pr.AuthorId);
        Assert.Null(pr.HeadRepo);
        Assert.Null(pr.MergeableState);
        Assert.Empty(pr.Labels);
    }

    [Fact]
    public async Task ListCheckRuns_ReadsEveryPageUntilTheLastShortOne()
    {
        var (client, handler, tokens) = Create(request => Page(request, 205, (from, count) => CheckRunsPage(from, count, 205)));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(205, runs.Count);
        Assert.Equal(Enumerable.Range(1, 205), runs.Select(r => (int)r.Id));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal($"/repos/octo/widgets/commits/{Sha}/check-runs", r.Uri.AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Contains("per_page=100", r.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(["1", "2", "3"], handler.Requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["page"]));
        var (_, permissions) = Assert.Single(tokens.Requests.DistinctBy(r => string.Join(',', r.Permissions)));
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "read" }, permissions);
    }

    [Fact]
    public async Task ListCheckRuns_AskForTheNextPageWhenAPageIsExactlyFull()
    {
        var (client, handler, _) = Create(request => Page(request, 100, (from, count) => CheckRunsPage(from, count, 100)));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(100, runs.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListCheckRuns_ParsesStatusConclusionAndApp()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""
            {"total_count":2,"check_runs":[
              {"id":1,"name":"build","status":"completed","conclusion":"success","head_sha":"abc","app":{"slug":"github-actions"}},
              {"id":2,"name":"scan","status":"in_progress","conclusion":null,"head_sha":"abc","app":null}]}
            """));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(new GitHubCheckRun(1, "build", "completed", "success", "github-actions"), runs[0]);
        Assert.Equal(new GitHubCheckRun(2, "scan", "in_progress", null, null), runs[1]);
    }

    [Fact]
    public async Task ListCheckRuns_WithNoRuns_ReturnsEmpty()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"total_count":0,"check_runs":[]}"""));

        Assert.Empty(await client.ListCheckRunsAsync("octo/widgets", Sha, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListReviews_ReadsEveryPage()
    {
        var (client, handler, tokens) = Create(request => Page(request, 130, (from, count) => JsonSerializer.Serialize(
            Enumerable.Range(from, count).Select(i => new { id = i, user = new { login = $"user-{i}" }, state = "APPROVED", commit_id = Sha, submitted_at = "2026-10-11T12:00:00Z" }))));

        var reviews = await client.ListReviewsAsync("octo/widgets", 7, Ct);

        Assert.Equal(130, reviews.Count);
        Assert.Equal(new GitHubReview(1, "user-1", "APPROVED", Sha, DateTimeOffset.Parse("2026-10-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), reviews[0]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/repos/octo/widgets/pulls/7/reviews", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task ListReviews_ToleratesAMissingUserAndTimestamp()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""[{"id":5,"user":null,"state":"PENDING","commit_id":null}]"""));

        var review = Assert.Single(await client.ListReviewsAsync("octo/widgets", 7, Ct));

        Assert.Null(review.AuthorLogin);
        Assert.Null(review.SubmittedAt);
    }

    [Fact]
    public async Task ListLabels_ReadsEveryPageAndReturnsTheNames()
    {
        var (client, handler, tokens) = Create(request => Page(request, 101, (from, count) => JsonSerializer.Serialize(
            Enumerable.Range(from, count).Select(i => new { id = i, name = $"label-{i}" }))));

        var labels = await client.ListLabelsAsync("octo/widgets", 7, Ct);

        Assert.Equal(Enumerable.Range(1, 101).Select(i => $"label-{i}"), labels);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/repos/octo/widgets/issues/7/labels", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task EveryRequestIsAGet()
    {
        var (client, handler, _) = Create(request => request.Uri.AbsolutePath.EndsWith("check-runs", StringComparison.Ordinal)
            ? StubHttpHandler.Json("""{"total_count":0,"check_runs":[]}""")
            : request.Uri.AbsolutePath.Contains("/pulls/7", StringComparison.Ordinal) && !request.Uri.AbsolutePath.EndsWith("reviews", StringComparison.Ordinal)
                ? StubHttpHandler.Json("""{"number":7,"state":"open","head":{"ref":"r","sha":"s"},"base":{"ref":"main"}}""")
                : StubHttpHandler.Json("[]"));

        await client.GetPullRequestAsync("octo/widgets", 7, Ct);
        await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);
        await client.ListReviewsAsync("octo/widgets", 7, Ct);
        await client.ListLabelsAsync("octo/widgets", 7, Ct);

        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task AFailureStatus_RaisesAnExceptionWithTheStatusCode()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestAsync("octo/widgets", 7, Ct));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task AnEmptyOrNullBody_RaisesAnException()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("null"));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestAsync("octo/widgets", 7, Ct));
    }

    [Fact]
    public async Task ABodyThatIsNotJson_RaisesAnException()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("<html>oops</html>"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestAsync("octo/widgets", 7, Ct));

        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task APagedListThatNeverEnds_StopsAtTheLimit()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json(JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(i => new { id = i, name = "x" }))));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ListLabelsAsync("octo/widgets", 7, Ct));

        Assert.Equal(GitHubApiClient.MaxPages, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidArguments_AreRejectedBeforeAnyRequest()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("[]"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPullRequestAsync("not-a-repo", 7, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetPullRequestAsync("octo/widgets", 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListReviewsAsync("octo/widgets", -1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListLabelsAsync("octo/widgets", 0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "../../x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "abc?x=1", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "", Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Requests);
    }
}
