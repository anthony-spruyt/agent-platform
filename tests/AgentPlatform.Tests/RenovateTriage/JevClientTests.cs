using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.RenovateTriage;

public class JevClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 5, 9, 123, TimeSpan.Zero);

    private static (JevClient Client, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond, string apiKey = "sk-test-virtual", Action<LiteLlmOptions>? configure = null)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false);
        var litellm = new LiteLlmOptions { BaseUrl = "http://litellm.example.test:4000", ApiKey = apiKey };
        configure?.Invoke(litellm);
        var options = Options.Create(litellm);
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

        var answers = await client.AskAsync("notes", null, Ct);

        Assert.Equal(new JevAnswers(0.04, "patch", 0.78), answers);
    }

    [Fact]
    public async Task Ask_PostsToTheNativeSystemOneRouteWithTheVirtualKey()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("notes", null, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://litellm.example.test:4000/v1/systemone", request.Uri.ToString());
        Assert.Equal("Bearer sk-test-virtual", request.Authorization);
    }

    [Fact]
    public async Task Ask_PostsToTheConfiguredPathWithTheConfiguredModel()
    {
        var (client, handler) = Create(
            _ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")),
            configure: o =>
            {
                o.JevPath = "typesafe/v1/systemone";
                o.JevModel = "jev-2.0.0";
            });

        await client.AskAsync("notes", null, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://litellm.example.test:4000/typesafe/v1/systemone", request.Uri.ToString());
        Assert.Equal("jev-2.0.0", (string?)JsonNode.Parse(request.Body!)!["model"]);
    }

    [Fact]
    public async Task Ask_SendsTheStateAndTheQuestionsTheOldWorkflowSent()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("## Release notes\n- fix: \"quoted\" thing", null, Ct);

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!.AsObject();
        Assert.Equal("jev-1.14.0", (string?)body["model"]);
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
    public async Task Ask_SendsThePrDiffInTheStateNextToTheBody()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("notes", "### package.json\n@@\n-\"a\": \"1.0.0\"\n+\"a\": \"1.0.1\"\n\n", Ct);

        var state = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!.AsObject();
        Assert.Equal(["pr_body", "pr_diff", "current_date"], state.Select(p => p.Key));
        Assert.Equal("### package.json\n@@\n-\"a\": \"1.0.0\"\n+\"a\": \"1.0.1\"\n\n", (string?)state["pr_diff"]);
    }

    [Fact]
    public async Task Ask_SendsANullPrDiffAsNull()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("notes", null, Ct);

        Assert.Null(JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_diff"]);
    }

    [Fact]
    public async Task Ask_SendsTheN8nQuestionsUnchanged_ExceptThatBumpTypeAlsoPointsAtThePrDiff()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync("notes", "diff", Ct);

        var sent = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["questions"]!;
        var expected = JsonNode.Parse(Fixture("jev-questions-n8n.json"))!;
        const string OldFirstSentence = "Find the old and new version in `pr_body` (usually a table cell like `44.69.8` -> `44.83.1`).";
        const string NewFirstSentence = "Find the old and new version in `pr_diff` (the PR's actual file changes) or `pr_body` (usually a table cell like `44.69.8` -> `44.83.1`).";
        var focus = (string)expected["bump_type"]!["instructions"]!["focus"]!;
        Assert.StartsWith(OldFirstSentence, focus, StringComparison.Ordinal);
        expected["bump_type"]!["instructions"]!["focus"] = NewFirstSentence + focus[OldFirstSentence.Length..];
        Assert.Equal(expected.ToJsonString(), sent.ToJsonString());
    }

    [Fact]
    public async Task Ask_LeavesAPrDiffUnderTheCapUntouched()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));
        var diff = new string('a', JevClient.MaxPrDiffBytes);

        await client.AskAsync("notes", diff, Ct);

        Assert.Equal(diff, (string?)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_diff"]);
    }

    [Fact]
    public async Task Ask_CutsAnOversizedPrDiffAtTheCap_OnAUtf8Boundary()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));
        var diff = "head" + new string('\u20ac', 20_000);

        await client.AskAsync("notes", diff, Ct);

        var sent = (string?)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_diff"];
        Assert.NotNull(sent);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sent) <= JevClient.MaxPrDiffBytes);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sent) > JevClient.MaxPrDiffBytes - 4);
        Assert.DoesNotContain('\ufffd', sent);
        Assert.StartsWith(sent, diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_SendsANullPrBodyAsNull()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync(null, null, Ct);

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

        var ex = await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));

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

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));
    }

    [Fact]
    public async Task Ask_FailsWhenTheServiceCannotBeReached()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));
    }

    [Fact]
    public async Task Ask_LetsACallerCancellationThrough()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AskAsync("notes", null, cts.Token));
    }

    [Fact]
    public async Task Ask_TurnsATimeoutIntoAJevFailure()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException());

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));
    }

    [Fact]
    public async Task Ask_TurnsAnyOtherSendFailureIntoAJevFailure()
    {
        var (client, _) = Create(_ => throw new InvalidOperationException("handler broke"));

        var ex = await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task Ask_TurnsAResilienceTimeoutIntoAJevFailure()
    {
        var (client, _) = Create(_ => throw new Polly.Timeout.TimeoutRejectedException("attempt timed out"));

        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("notes", null, Ct));
    }

    [Fact]
    public async Task Ask_LeavesAPrBodyUnderTheCapUntouched()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));
        var body = new string('a', JevClient.MaxPrBodyBytes);

        await client.AskAsync(body, null, Ct);

        Assert.Equal(body, (string?)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_body"]);
    }

    [Fact]
    public async Task Ask_CutsAnOversizedPrBodyAtTheCap_OnAUtf8Boundary()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));
        var body = "head" + new string('\u20ac', 20_000);

        await client.AskAsync(body, null, Ct);

        var sent = (string?)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_body"];
        Assert.NotNull(sent);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sent) <= JevClient.MaxPrBodyBytes);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sent) > JevClient.MaxPrBodyBytes - 4);
        Assert.StartsWith("head", sent, StringComparison.Ordinal);
        Assert.DoesNotContain('\ufffd', sent);
        Assert.StartsWith(sent, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_CutsAnOversizedAsciiPrBodyExactlyAtTheCap()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json")));

        await client.AskAsync(new string('a', JevClient.MaxPrBodyBytes + 500), null, Ct);

        var sent = (string?)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["pr_body"];
        Assert.Equal(JevClient.MaxPrBodyBytes, sent!.Length);
    }

    [Fact]
    public async Task TheClientFromTheRealServiceCollection_WaitsForASlowJevAndSendsItOnce()
    {
        var attempts = 0;
        var slow = new DelegatingStub(async (_, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
            return StubHttpHandler.Json(Fixture("jev-response-patch-with-changelog.json"));
        });
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LiteLlm:BaseUrl"] = "http://litellm.example.test:4000",
            ["LiteLlm:ApiKey"] = "sk-test-virtual",
        });
        builder.AddServiceDefaults();
        builder.Services.AddRenovateTriage(builder.Configuration);
        builder.Services.AddHttpClient<IJevClient, JevClient>().ConfigurePrimaryHttpMessageHandler(() => slow);
        using var provider = builder.Services.BuildServiceProvider();

        var answers = await provider.GetRequiredService<IJevClient>().AskAsync("notes", null, Ct);

        Assert.Equal(new JevAnswers(0.04, "patch", 0.78), answers);
        Assert.Equal(1, attempts);
    }

    private sealed class DelegatingStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
