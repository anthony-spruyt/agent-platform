using System.Net;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

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
        return (new GitHubApiClient(http, tokens, Options.Create(new GitHubAppOptions { AppId = "3233443" })), handler, tokens);
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

    private static List<RecordedRequest> CheckRunRequests(StubHttpHandler handler) =>
        [.. handler.Requests.Where(r => r.Uri.AbsolutePath == $"/repos/octo/widgets/commits/{Sha}/check-runs")];

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
             "base":{"ref":"main","repo":{"full_name":"octo/widgets-base"}},
             "commits":3,"changed_files":12,"labels":[{"name":"dependencies"},{"name":"blocked"}]}
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
        Assert.Equal("octo/widgets-base", pr.BaseRepo);
        Assert.Equal("main", pr.BaseRef);
        Assert.Equal(["dependencies", "blocked"], pr.Labels);
        Assert.Equal(3, pr.Commits);
        Assert.Equal(12, pr.ChangedFiles);
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
        Assert.Null(pr.BaseRepo);
        Assert.Null(pr.MergeableState);
        Assert.Empty(pr.Labels);
        Assert.Equal(0, pr.Commits);
        Assert.Equal(0, pr.ChangedFiles);
    }

    [Fact]
    public async Task ListCheckRuns_ReadsEveryPageUntilTheLastShortOne()
    {
        var (client, handler, tokens) = Create(request => Page(request, 205, (from, count) => CheckRunsPage(from, count, 205)));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(205, runs.Count);
        Assert.Equal(Enumerable.Range(1, 205), runs.Select(r => (int)r.Id));
        var requests = CheckRunRequests(handler);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Contains("per_page=100", r.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(["1", "2", "3"], requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["page"]));
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task ListCheckRuns_ReadsEveryPageOfTheCommitStatuses()
    {
        var (client, handler, tokens) = Create(request => request.Uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
            ? Page(request, 130, (from, count) => JsonSerializer.Serialize(new
            {
                state = "failure",
                total_count = 130,
                statuses = Enumerable.Range(from, count).Select(i => new { id = i, context = $"status-{i}", state = "success" }),
            }))
            : StubHttpHandler.Json("""{"total_count":0,"check_runs":[]}"""));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(Enumerable.Range(1, 130).Select(i => $"status-{i}"), runs.Select(r => r.Name));
        var requests = handler.Requests.Where(r => r.Uri.AbsolutePath == $"/repos/octo/widgets/commits/{Sha}/status").ToList();
        Assert.Equal(["1", "2"], requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["page"]));
        Assert.All(requests, r => Assert.Contains("per_page=100", r.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(new Dictionary<string, string> { ["statuses"] = "read" }, tokens.Requests[1].Permissions);
    }

    [Fact]
    public async Task ListCheckRuns_AskForTheNextPageWhenAPageIsExactlyFull()
    {
        var (client, handler, _) = Create(request => Page(request, 100, (from, count) => CheckRunsPage(from, count, 100)));

        var runs = await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);

        Assert.Equal(100, runs.Count);
        Assert.Equal(2, CheckRunRequests(handler).Count);
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
        Assert.Equal([$"/repos/octo/widgets/commits/{Sha}/check-runs", $"/repos/octo/widgets/commits/{Sha}/status"], handler.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ListReviews_ReadsEveryPage()
    {
        var (client, handler, tokens) = Create(request => Page(request, 130, (from, count) => JsonSerializer.Serialize(
            Enumerable.Range(from, count).Select(i => new { id = i, user = new { login = $"user-{i}" }, author_association = "COLLABORATOR", state = "APPROVED", commit_id = Sha, submitted_at = "2026-10-11T12:00:00Z" }))));

        var reviews = await client.ListReviewsAsync("octo/widgets", 7, Ct);

        Assert.Equal(130, reviews.Count);
        Assert.Equal(new GitHubReview(1, "user-1", "COLLABORATOR", "APPROVED", Sha, DateTimeOffset.Parse("2026-10-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), reviews[0]);
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
        Assert.Null(review.AuthorAssociation);
        Assert.Null(review.SubmittedAt);
    }

    [Fact]
    public async Task ListCheckSuites_ReadsEveryPageAndParsesTheFieldsTheTriageNeeds()
    {
        var (client, handler, tokens) = Create(request => Page(request, 120, (from, count) => JsonSerializer.Serialize(new
        {
            total_count = 120,
            check_suites = Enumerable.Range(from, count).Select(i => new
            {
                id = i,
                status = i == 1 ? "queued" : "completed",
                conclusion = i == 1 ? null : "success",
                app = i == 2 ? null : new { slug = "github-actions" },
                latest_check_runs_count = i,
            }),
        })));

        var suites = await client.ListCheckSuitesAsync("octo/widgets", Sha, Ct);

        Assert.Equal(120, suites.Count);
        Assert.Equal(new GitHubCheckSuite(1, "queued", null, "github-actions", 1), suites[0]);
        Assert.Equal(new GitHubCheckSuite(2, "completed", "success", null, 2), suites[1]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal($"/repos/octo/widgets/commits/{Sha}/check-suites", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["checks"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task ListCheckSuites_RejectsABadSha()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"check_suites":[]}"""));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckSuitesAsync("octo/widgets", "../x", Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListCommits_ReadsEveryPageAndParsesAuthorshipAndVerification()
    {
        var (client, handler, tokens) = Create(request => Page(request, 130, (from, count) => JsonSerializer.Serialize(
            Enumerable.Range(from, count).Select(i => new
            {
                sha = $"sha-{i}",
                author = i == 2 ? null : new { login = "renovate[bot]", id = 29139614 },
                committer = new { login = "web-flow", id = 19864447 },
                commit = new
                {
                    message = $"chore(deps): update dep {i}\n\n| datasource | package | from | to |",
                    author = new { email = "29139614+renovate[bot]@users.noreply.github.com" },
                    committer = new { email = "noreply@github.com" },
                    verification = new { verified = i != 3, reason = "valid" },
                },
            }))));

        var commits = await client.ListCommitsAsync("octo/widgets", 7, Ct);

        Assert.Equal(130, commits.Count);
        Assert.Equal(new GitHubCommit("sha-1", "renovate[bot]", 29139614, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true, "chore(deps): update dep 1\n\n| datasource | package | from | to |"), commits[0]);
        Assert.Null(commits[1].AuthorLogin);
        Assert.Null(commits[1].AuthorId);
        Assert.False(commits[2].Verified);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/repos/octo/widgets/pulls/7/commits", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task ListCommits_ToleratesMissingCommitDetails()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""[{"sha":"abc","author":null,"commit":null}]"""));

        var commit = Assert.Single(await client.ListCommitsAsync("octo/widgets", 7, Ct));

        Assert.Null(commit.AuthorEmail);
        Assert.Null(commit.CommitterEmail);
        Assert.False(commit.Verified);
        Assert.Equal(string.Empty, commit.Message);
    }

    [Fact]
    public async Task ListCommits_KeepsTheParentsOfEachCommit()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""
            [{"sha":"m1","parents":[{"sha":"p1"},{"sha":"p2"}]},{"sha":"c1","parents":[{"sha":"p0"}]},{"sha":"n1"}]
            """));

        var commits = await client.ListCommitsAsync("octo/widgets", 7, Ct);

        Assert.Equal(["p1", "p2"], commits[0].Parents);
        Assert.Equal(["p0"], commits[1].Parents);
        Assert.Null(commits[2].Parents);
    }

    [Fact]
    public async Task Compare_ReadsTheStatusMergeBaseAndFilesBetweenTwoRefs_WithAContentsReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""
            {"status":"behind","merge_base_commit":{"sha":"abc123"},
             "files":[{"filename":"a.txt","status":"modified","sha":"s1"},
                      {"filename":"b.txt","status":"renamed","sha":"s2","previous_filename":"old.txt"}]}
            """));

        var comparison = await client.CompareAsync("octo/widgets", "release/1.x", Sha, Ct);

        Assert.Equal("behind", comparison!.Status);
        Assert.Equal("abc123", comparison.MergeBaseSha);
        Assert.Equal([new GitHubCompareFile("a.txt", "modified", "s1"), new GitHubCompareFile("b.txt", "renamed", "s2", "old.txt")], comparison.Files);
        Assert.Equal($"/repos/octo/widgets/compare/release/1.x...{Sha}", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task Compare_KeepsAMissingFileListMissing_InsteadOfReadingItAsEmpty()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"status":"identical"}"""));

        var comparison = await client.CompareAsync("octo/widgets", "main", Sha, Ct);

        Assert.Equal("identical", comparison!.Status);
        Assert.Null(comparison.MergeBaseSha);
        Assert.Null(comparison.Files);
    }

    [Fact]
    public async Task Compare_KeepsAnEmptyFileListEmpty()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"status":"identical","files":[]}"""));

        var comparison = await client.CompareAsync("octo/widgets", "main", Sha, Ct);

        Assert.Empty(comparison!.Files!);
    }

    [Fact]
    public async Task GetDefaultBranch_ReadsItFromTheRepository_WithAContentsReadToken()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"full_name":"octo/widgets","default_branch":"trunk"}"""));

        var branch = await client.GetDefaultBranchAsync("octo/widgets", Ct);

        Assert.Equal("trunk", branch);
        Assert.Equal("/repos/octo/widgets", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read" }, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task GetDefaultBranch_IsEmptyWhenTheRepositoryNamesNone()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}"));

        Assert.Equal(string.Empty, await client.GetDefaultBranchAsync("octo/widgets", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetDefaultBranch_ThrowsOnAFailure(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", status));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetDefaultBranchAsync("octo/widgets", Ct));

        Assert.Equal(status, failure.StatusCode);
    }

    [Fact]
    public async Task GetDefaultBranch_RefusesARepoNameThatIsNotOwnerSlashName()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetDefaultBranchAsync("not-a-repo", Ct));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Compare_GivesNoComparisonWhenGitHubSaysThereIsNone(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"No common ancestor"}""", status));

        Assert.Null(await client.CompareAsync("octo/widgets", "main", Sha, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Compare_ThrowsOnAnyOtherFailure(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("{}", status));

        var failure = await Assert.ThrowsAsync<GitHubApiException>(() => client.CompareAsync("octo/widgets", "main", Sha, Ct));

        Assert.Equal(status, failure.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("../main")]
    [InlineData("main/.")]
    public async Task Compare_RefusesARefThatCouldLeaveItsPlace(string bad)
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CompareAsync("octo/widgets", bad, Sha, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Compare_EscapesTheCharactersOfARef()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"status":"behind"}"""));

        await client.CompareAsync("octo/widgets", "feat/a b#1", Sha, Ct);

        Assert.Equal($"/repos/octo/widgets/compare/feat/a%20b%231...{Sha}", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task ListPullRequestFiles_ReadsEveryPageAndKeepsThePatchOfEachFile()
    {
        var (client, handler, tokens) = Create(request => Page(request, 101, (from, count) => JsonSerializer.Serialize(
            Enumerable.Range(from, count).Select(i => i == 2
                ? (object)new { sha = "x", filename = "image.png", status = "added" }
                : new { sha = "x", filename = $"src/file-{i}.txt", status = "modified", patch = $"@@ -1 +1 @@\n-old {i}\n+new {i}" }))));

        var files = await client.ListPullRequestFilesAsync("octo/widgets", 7, Ct);

        Assert.Equal(101, files.Count);
        Assert.Equal(new GitHubPullRequestFile("src/file-1.txt", "@@ -1 +1 @@\n-old 1\n+new 1"), files[0]);
        Assert.Equal(new GitHubPullRequestFile("image.png", null, "added"), files[1]);
        Assert.Equal("modified", files[0].Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/repos/octo/widgets/pulls/7/files", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task ListPullRequestFiles_ToleratesAMissingFilename()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""[{"patch":"@@"}]"""));

        var file = Assert.Single(await client.ListPullRequestFilesAsync("octo/widgets", 7, Ct));

        Assert.Equal(string.Empty, file.Filename);
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
            : request.Uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
            ? StubHttpHandler.Json("""{"state":"pending","total_count":0,"statuses":[]}""")
            : request.Uri.AbsolutePath.EndsWith("check-suites", StringComparison.Ordinal)
            ? StubHttpHandler.Json("""{"total_count":0,"check_suites":[]}""")
            : request.Uri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? StubHttpHandler.Json("""{"number":7,"state":"open","head":{"ref":"r","sha":"s"},"base":{"ref":"main"}}""")
                : StubHttpHandler.Json("[]"));

        await client.GetPullRequestAsync("octo/widgets", 7, Ct);
        await client.ListCheckRunsAsync("octo/widgets", Sha, Ct);
        await client.ListReviewsAsync("octo/widgets", 7, Ct);
        await client.ListLabelsAsync("octo/widgets", 7, Ct);
        await client.ListCommitsAsync("octo/widgets", 7, Ct);
        await client.ListCheckSuitesAsync("octo/widgets", Sha, Ct);
        await client.ListPullRequestFilesAsync("octo/widgets", 7, Ct);

        Assert.Equal(8, handler.Requests.Count);
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
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListPullRequestFilesAsync("octo/widgets", 0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "../../x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "abc?x=1", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListCheckRunsAsync("octo/widgets", "", Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Requests);
    }

    private static readonly Dictionary<string, string> _pullRequestsWrite = new() { ["pull_requests"] = "write" };

    [Fact]
    public async Task ApproveAsync_PostsAnApprovingReviewPinnedToTheCommit()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":9,"state":"APPROVED"}"""));

        await client.ApproveAsync("octo/widgets", 7, Sha, "**Auto-approved:** digest bump.", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/octo/widgets/pulls/7/reviews", request.Uri.AbsolutePath);
        Assert.Equal("Bearer ghs_install_token", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("APPROVE", body.RootElement.GetProperty("event").GetString());
        Assert.Equal(Sha, body.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("**Auto-approved:** digest bump.", body.RootElement.GetProperty("body").GetString());
        Assert.Equal(_pullRequestsWrite, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task DismissReviewAsync_PutsADismissalWithTheMessage()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":9,"state":"DISMISSED"}"""));

        await client.DismissReviewAsync("octo/widgets", 7, 9, "A new commit needs review.", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/repos/octo/widgets/pulls/7/reviews/9/dismissals", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("A new commit needs review.", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(_pullRequestsWrite, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task AddLabelsAsync_PostsTheLabelNames()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""[{"name":"agent/needs-human-review"}]"""));

        await client.AddLabelsAsync("octo/widgets", 7, ["agent/needs-human-review"], Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/octo/widgets/issues/7/labels", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("agent/needs-human-review", Assert.Single(body.RootElement.GetProperty("labels").EnumerateArray()).GetString());
        Assert.Equal(_pullRequestsWrite, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task ListIssueCommentsAsync_ReadsEveryPage()
    {
        var (client, handler, tokens) = Create(request => Page(request, 130, (from, count) =>
            JsonSerializer.Serialize(Enumerable.Range(from, count).Select(i => new { id = i, body = $"comment {i}", user = new { login = "skynet-rw[bot]" } }))));

        var comments = await client.ListIssueCommentsAsync("octo/widgets", 7, Ct);

        Assert.Equal(130, comments.Count);
        Assert.Equal(new GitHubIssueComment(1, "skynet-rw[bot]", "comment 1"), comments[0]);
        Assert.All(handler.Requests, r => Assert.Equal("/repos/octo/widgets/issues/7/comments", r.Uri.AbsolutePath));
        Assert.Equal(new Dictionary<string, string> { ["pull_requests"] = "read" }, tokens.Requests[0].Permissions);
    }

    [Fact]
    public async Task CreateIssueCommentAsync_PostsTheBodyAndReturnsTheId()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""{"id":55}""", HttpStatusCode.Created));

        var id = await client.CreateIssueCommentAsync("octo/widgets", 7, "hello", Ct);

        Assert.Equal(55, id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/octo/widgets/issues/7/comments", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("hello", body.RootElement.GetProperty("body").GetString());
        Assert.Equal(_pullRequestsWrite, Assert.Single(tokens.Requests).Permissions);
    }

    [Fact]
    public async Task UpdateIssueCommentAsync_PatchesTheComment()
    {
        var (client, handler, _) = Create(_ => StubHttpHandler.Json("""{"id":55}"""));

        await client.UpdateIssueCommentAsync("octo/widgets", 55, "edited", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/repos/octo/widgets/issues/comments/55", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("edited", body.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AWriteThatFails_RaisesAnExceptionWithTheStatusCode(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json("""{"message":"no"}""", status));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.AddLabelsAsync("octo/widgets", 7, ["x"], Ct));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task InvalidWriteArguments_AreRejectedBeforeAnyRequest()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ApproveAsync("octo/widgets", 7, "../x", "b", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ApproveAsync("octo/widgets", 0, Sha, "b", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.DismissReviewAsync("octo/widgets", 7, 0, "m", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddLabelsAsync("octo/widgets", 7, [], Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UpdateIssueCommentAsync("octo/widgets", 0, "b", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateIssueCommentAsync("not-a-repo", 7, "b", Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Requests);
    }
}
