using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Sweeper;

public class GitHubApiClientSweeperTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

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
        return (new GitHubApiClient(http, tokens, Options.Create(new GitHubAppOptions { AppId = "3233443" })), handler, tokens);
    }

    private static object Pr(int number, DateTimeOffset updated, string branch = "renovate/dep") => new
    {
        number,
        state = "open",
        updated_at = updated,
        user = new { login = "renovate[bot]", id = 29139614 },
        head = new { @ref = branch, sha = $"sha{number}", repo = new { full_name = "o/r" } },
        @base = new { @ref = "main", repo = new { full_name = "o/r" } },
        labels = new[] { new { name = "agent/needs-triage" } },
    };

    [Fact]
    public async Task ListOpenPullRequests_ReadsNewestUpdatedFirst_AndStopsAtTheFirstPageHoldingAPrOlderThanTheCutoff()
    {
        var (client, handler, tokens) = Create(request => StubHttpHandler.Json(JsonSerializer.Serialize(
            request.Uri.Query.EndsWith("&page=1", StringComparison.Ordinal)
                ? Enumerable.Range(1, 100).Select(i => Pr(i, Since.AddHours(1000 - i))).ToArray()
                : [Pr(101, Since.AddHours(5)), Pr(102, Since.AddHours(-1)), Pr(103, Since.AddHours(-2))])));

        var prs = await client.ListOpenPullRequestsAsync("o/r", Since, Ct);

        Assert.Equal(101, prs.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("state=open&sort=updated&direction=desc&per_page=100", r.Uri.Query, StringComparison.Ordinal));
        Assert.All(tokens.Requests, p => Assert.Equal("read", p["pull_requests"]));
    }

    [Fact]
    public async Task ListOpenPullRequests_MapsTheFieldsTheSweeperNeeds()
    {
        var (client, _, _) = Create(_ => StubHttpHandler.Json(JsonSerializer.Serialize(new[] { Pr(7, Since.AddHours(2), "renovate/foo") })));

        var pr = Assert.Single(await client.ListOpenPullRequestsAsync("o/r", Since, Ct));

        Assert.Equal(7, pr.Number);
        Assert.Equal(29139614, pr.AuthorId);
        Assert.Equal("sha7", pr.HeadSha);
        Assert.Equal("o/r", pr.HeadRepo);
        Assert.Equal("o/r", pr.BaseRepo);
        Assert.Equal(["agent/needs-triage"], pr.Labels);
    }

    [Fact]
    public async Task ListIssueEvents_ReadsTheActorTheLabelAndTheTime()
    {
        var (client, handler, tokens) = Create(_ => StubHttpHandler.Json("""
            [{"event":"unlabeled","created_at":"2026-10-15T10:00:00Z","actor":{"login":"anthony-spruyt"},"label":{"name":"agent/needs-triage"}},
             {"event":"closed","created_at":"2026-10-15T11:00:00Z","actor":null}]
            """));

        var events = await client.ListIssueEventsAsync("o/r", 7, Ct);

        Assert.Equal(
            [new GitHubIssueEvent("unlabeled", "anthony-spruyt", "agent/needs-triage", new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)), new GitHubIssueEvent("closed", null, null, new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero))],
            events);
        Assert.Equal("/repos/o/r/issues/7/events", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal("read", Assert.Single(tokens.Requests)["pull_requests"]);
    }
}
