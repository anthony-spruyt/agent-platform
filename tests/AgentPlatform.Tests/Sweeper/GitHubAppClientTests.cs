using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.Sweeper;

public class GitHubAppClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedJwt : IGitHubAppJwtSource
    {
        public string CreateJwt() => "app.jwt.value";
    }

    private static (GitHubAppClient Client, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubAppClient(http, new FixedJwt()), handler);
    }

    private static HttpResponseMessage WithNext(string json, string? nextCursor)
    {
        var response = StubHttpHandler.Json(json);
        if (nextCursor is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/app/hook/deliveries?per_page=100&cursor={nextCursor}>; rel=\"next\", <https://api.github.com/app/hook/deliveries?per_page=100>; rel=\"first\"");
        }

        return response;
    }

    [Fact]
    public async Task ListHookDeliveries_ReadsTheDeliveriesWithTheAppJwt_AndReturnsTheNextCursor()
    {
        var (client, handler) = Create(_ => WithNext("""
            [{"id":11,"guid":"g-1","delivered_at":"2026-10-15T11:00:00Z","redelivery":false,"status_code":502,"event":"check_suite","action":"completed"},
             {"id":10,"guid":"g-2","delivered_at":"2026-10-15T10:00:00Z","redelivery":true,"status_code":200,"event":"ping","action":null}]
            """, "v1_abc"));

        var page = await client.ListHookDeliveriesAsync(null, Ct);

        Assert.Equal("v1_abc", page.NextCursor);
        Assert.Equal(
            [new HookDelivery(11, "g-1", new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero), false, 502, "check_suite"), new HookDelivery(10, "g-2", new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), true, 200, "ping")],
            page.Deliveries);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer app.jwt.value", request.Authorization);
        Assert.Equal("/app/hook/deliveries", request.Uri.AbsolutePath);
        Assert.Contains("per_page=100", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListHookDeliveries_PassesTheCursorOn_AndHasNoNextCursorOnTheLastPage()
    {
        var (client, handler) = Create(_ => WithNext("[]", null));

        var page = await client.ListHookDeliveriesAsync("v1_abc", Ct);

        Assert.Null(page.NextCursor);
        Assert.Contains("cursor=v1_abc", Assert.Single(handler.Requests).Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeliver_PostsAnAttempt_AndReportsItAccepted()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        Assert.True(await client.RedeliverAsync(77, Ct));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/app/hook/deliveries/77/attempts", request.Uri.AbsolutePath);
        Assert.Equal("Bearer app.jwt.value", request.Authorization);
    }

    [Fact]
    public async Task Redeliver_TreatsA422AsRefused_AndOtherFailuresAsErrors()
    {
        var (refused, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var (broken, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        Assert.False(await refused.RedeliverAsync(77, Ct));
        await Assert.ThrowsAsync<GitHubApiException>(() => broken.RedeliverAsync(77, Ct));
    }

    [Fact]
    public async Task ListInstallationRepositories_FindsTheOwnersInstallation_AndListsItsRepositoriesWithAReadOnlyToken()
    {
        var (client, handler) = Create(request => request.Uri.AbsolutePath switch
        {
            "/app/installations" => StubHttpHandler.Json("""[{"id":1,"account":{"login":"someone-else"}},{"id":42,"account":{"login":"Anthony-Spruyt"}}]"""),
            "/app/installations/42/access_tokens" => StubHttpHandler.Json("""{"token":"ghs_scoped","expires_at":"2026-10-15T13:00:00Z"}""", HttpStatusCode.Created),
            "/installation/repositories" => StubHttpHandler.Json("""{"total_count":2,"repositories":[{"full_name":"anthony-spruyt/one"},{"full_name":"anthony-spruyt/two"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var repos = await client.ListInstallationRepositoriesAsync("anthony-spruyt", Ct);

        Assert.Equal(["anthony-spruyt/one", "anthony-spruyt/two"], repos);
        Assert.Equal("Bearer app.jwt.value", handler.Requests[0].Authorization);
        Assert.Equal("Bearer app.jwt.value", handler.Requests[1].Authorization);
        Assert.Contains("\"pull_requests\":\"read\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer ghs_scoped", handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task ListInstallationRepositories_IsEmpty_WhenTheOwnerHasNoInstallation()
    {
        var (client, _) = Create(_ => StubHttpHandler.Json("""[{"id":1,"account":{"login":"someone-else"}}]"""));

        Assert.Empty(await client.ListInstallationRepositoriesAsync("anthony-spruyt", Ct));
    }
}
