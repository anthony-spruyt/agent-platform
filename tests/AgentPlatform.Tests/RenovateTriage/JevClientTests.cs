using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.RenovateTriage;

public class JevClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 5, 9, 123, TimeSpan.Zero);

    private static (JevClient Client, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond, string apiKey = "sk-test-virtual")
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false);
        var options = Options.Create(new LiteLlmOptions { BaseUrl = "http://litellm.example.test:4000", ApiKey = apiKey });
        return (new JevClient(http, options, new FixedTime(Now)), handler);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", name));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Ask_ParsesARealJevResponse()
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        var answers = await client.AskAsync("notes", Ct);

        Assert.Equal(new JevAnswers(0.04, "patch", 0.78), answers);
    }

    [Fact]
    public async Task Ask_PostsToTheSystemOneEndpointWithTheVirtualKey()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("notes", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://litellm.example.test:4000/typesafe/v1/systemone", request.Uri.ToString());
        Assert.Equal("Bearer sk-test-virtual", request.Authorization);
    }

    [Fact]
    public async Task Ask_SendsTheStateAndTheQuestionsTheOldWorkflowSent()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("## Release notes\n- fix: \"quoted\" thing", Ct);

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!.AsObject();
        Assert.Equal("jev-latest", (string?)body["model"]);
        Assert.Equal("## Release notes\n- fix: \"quoted\" thing", (string?)body["state"]!["pr_body"]);
        Assert.Equal("2026-10-12T07:05:09.123Z", (string?)body["state"]!["current_date"]);
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["breaking", "bump_type", "changelog"], questions.Select(q => q.Key));
        Assert.Equal("noul", (string?)questions["breaking"]!["type"]);
        Assert.Equal("choice", (string?)questions["bump_type"]!["type"]);
        Assert.Equal("noul", (string?)questions["changelog"]!["type"]);
        Assert.Equal("Does this Renovate dependency update include at least one breaking change?", (string?)questions["breaking"]!["instructions"]!["question"]);
        Assert.Equal(["digest", "calver", "major", "minor", "patch", "other"], questions["bump_type"]!["criteria"]!.AsObject().Select(c => c.Key));
        Assert.Equal("Are the full release notes or changelog contents directly embedded in the Renovate PR description? IE a list of changes / commits and not just a link to a release page?", (string?)questions["changelog"]!["instructions"]);
        Assert.Contains("⚠ BREAKING CHANGES", questions["breaking"]!["instructions"]!["signals_to_look_for"]!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_SendsANullPrBodyAsNull()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync(null, Ct);

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Null(body["state"]!["pr_body"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Ask_FailsOnAnErrorStatus(HttpStatusCode status)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json("{}", status));

        var ex = await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", Ct));

        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"answers":{}}""")]
    [InlineData("""{"answers":{"breaking":{"noul":0.1},"bump_type":{"choice":"patch"}}}""")]
    [InlineData("""{"answers":{"breaking":{"noul":0.1},"bump_type":{"choice":""},"changelog":{"noul":0.9}}}""")]
    [InlineData("""{"answers":{"breaking":{"noul":"high"},"bump_type":{"choice":"patch"},"changelog":{"noul":0.9}}}""")]
    [InlineData("""{"answers":{"breaking":{"noul":1.5},"bump_type":{"choice":"patch"},"changelog":{"noul":0.9}}}""")]
    public async Task Ask_FailsOnABodyThatLacksAnAnswer(string json)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(json));

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", Ct));
    }

    [Fact]
    public async Task Ask_FailsWhenTheServiceCannotBeReached()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", Ct));
    }

    [Fact]
    public async Task Ask_LetsACallerCancellationThrough()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AskAsync("notes", cts.Token));
    }

    [Fact]
    public async Task Ask_TurnsATimeoutIntoAJevFailure()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException());

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", Ct));
    }
}
