using System.Net;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapNoticesTests
{
    private const string Fork = "octo/livemap";

    private const string App = "skynet-rw[bot]";

    private static readonly DateTimeOffset Since = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LivemapNotices Client, StubHttpHandler Handler, LivemapPullRequestsTests.RecordingTokens Tokens) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.example/") };
        var tokens = new LivemapPullRequestsTests.RecordingTokens();
        return (new LivemapNotices(http, tokens, Options.Create(new LivemapOptions { Enabled = true, Repo = Fork, TrackingIssue = 12 })), handler, tokens);
    }

    private static string Comments(params (string Login, string Body)[] comments) =>
        new JsonArray([.. comments.Select(c => (JsonNode)new JsonObject { ["body"] = c.Body, ["user"] = new JsonObject { ["login"] = c.Login } })]).ToJsonString();

    private static Func<RecordedRequest, HttpResponseMessage> Answer(string existing) =>
        request => request.Method == HttpMethod.Get ? StubHttpHandler.Json(existing) : StubHttpHandler.Json("{}", HttpStatusCode.Created);

    [Fact]
    public async Task ANoticeNobodyPosted_IsPostedOnTheTrackingIssue_BehindItsMarker()
    {
        var (client, handler, _) = Create(Answer("[]"));

        var posted = await client.PostOnceAsync(777, App, Since, "Shipped.", Ct);

        Assert.True(posted);
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/repos/{Fork}/issues/12/comments", post.Uri.AbsolutePath);
        Assert.Equal($"{LivemapNotice.Marker(777)}\nShipped.", JsonNode.Parse(post.Body!)!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheTokenCanOnlyWriteIssuesOnTheFork()
    {
        var (client, _, tokens) = Create(Answer("[]"));

        await client.PostOnceAsync(777, App, Since, "Shipped.", Ct);

        var minted = Assert.Single(tokens.Minted);
        Assert.Equal(Fork, minted.Repo);
        Assert.Equal(new Dictionary<string, string> { ["issues"] = "write" }, minted.Permissions);
    }

    [Fact]
    public async Task OnlyCommentsSinceTheBuildWasDetected_AreRead()
    {
        var (client, handler, _) = Create(Answer("[]"));

        await client.PostOnceAsync(777, App, Since, "Shipped.", Ct);

        var read = handler.Requests.First(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/repos/{Fork}/issues/12/comments", read.Uri.AbsolutePath);
        Assert.Contains("since=2026-11-09T08:00:00Z", read.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("per_page=100", read.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoticeAlreadyPosted_IsNotPostedAgain()
    {
        var (client, handler, _) = Create(Answer(Comments(("someone", "hi"), (App, LivemapNotice.Marker(777) + "\nShipped."))));

        var posted = await client.PostOnceAsync(777, App, Since, "Shipped.", Ct);

        Assert.False(posted);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheAppLoginIsMatchedWithoutRegardToCase()
    {
        var (client, handler, _) = Create(Answer(Comments(("Skynet-RW[bot]", LivemapNotice.Marker(777) + "\nShipped."))));

        Assert.False(await client.PostOnceAsync(777, App, Since, "Shipped.", Ct));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AMarkerFromSomeoneElse_DoesNotCountAsTheNotice()
    {
        var (client, handler, _) = Create(Answer(Comments(("stranger", LivemapNotice.Marker(777) + "\nfake"))));

        Assert.True(await client.PostOnceAsync(777, App, Since, "Shipped.", Ct));
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(7770)]
    [InlineData(77)]
    public async Task TheNoticeOfAnotherBuild_DoesNotCount(long other)
    {
        var (client, _, _) = Create(Answer(Comments((App, LivemapNotice.Marker(other) + "\nShipped."))));

        Assert.True(await client.PostOnceAsync(777, App, Since, "Shipped.", Ct));
    }

    [Fact]
    public async Task AQuotedMarker_InTheMiddleOfAComment_DoesNotCount()
    {
        var (client, _, _) = Create(Answer(Comments((App, "see " + LivemapNotice.Marker(777)))));

        Assert.True(await client.PostOnceAsync(777, App, Since, "Shipped.", Ct));
    }

    [Fact]
    public async Task EveryPageIsSearched_UntilOneHoldsTheNotice()
    {
        var full = Comments([.. Enumerable.Range(0, 100).Select(i => ("someone", $"comment {i}"))]);
        var last = Comments((App, LivemapNotice.Marker(777) + "\nShipped."));
        var (client, handler, _) = Create(request => request.Method != HttpMethod.Get
            ? StubHttpHandler.Json("{}", HttpStatusCode.Created)
            : StubHttpHandler.Json(request.Uri.Query.Contains("page=2", StringComparison.Ordinal) ? last : full));

        Assert.False(await client.PostOnceAsync(777, App, Since, "Shipped.", Ct));

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task ATrackingIssueWithTooManyComments_FailsInsteadOfPostingTwice()
    {
        var full = Comments([.. Enumerable.Range(0, 100).Select(i => ("someone", $"comment {i}"))]);
        var (client, handler, _) = Create(request => request.Method != HttpMethod.Get ? StubHttpHandler.Json("{}", HttpStatusCode.Created) : StubHttpHandler.Json(full));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.PostOnceAsync(777, App, Since, "Shipped.", Ct));

        Assert.Equal(10, handler.Requests.Count(r => r.Method == HttpMethod.Get));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AFailedPost_Throws_SoTheNoticeIsRetried()
    {
        var (client, _, _) = Create(request => request.Method == HttpMethod.Get ? StubHttpHandler.Json("[]") : StubHttpHandler.Json("{}", HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.PostOnceAsync(777, App, Since, "Shipped.", Ct));
    }

    [Fact]
    public async Task ANoticeWithoutABuildOrALogin_IsRefusedBeforeAnyCall()
    {
        var (client, handler, tokens) = Create(Answer("[]"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.PostOnceAsync(0, App, Since, "Shipped.", Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.PostOnceAsync(777, " ", Since, "Shipped.", Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.PostOnceAsync(777, App, Since, " ", Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Minted);
    }
}
