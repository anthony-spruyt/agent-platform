using System.Net;
using System.Text.Json.Nodes;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.RenovateTriage;

public class JevClientCiTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 5, 9, 123, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (JevClient Client, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond, Action<LiteLlmOptions>? configure = null)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false);
        var litellm = new LiteLlmOptions { BaseUrl = "http://litellm.example.test:4000", ApiKey = "sk-test-virtual" };
        configure?.Invoke(litellm);
        var options = Options.Create(litellm);
        return (new JevClient(http, options, new FixedTime(Now)), handler);
    }

    private static HttpResponseMessage Answer(string choice) =>
        StubHttpHandler.Json(new JsonObject { ["answers"] = new JsonObject { ["ci_failure"] = new JsonObject { ["choice"] = choice } } }.ToJsonString());

    [Theory]
    [InlineData("flaky", CiFailureKind.Flaky)]
    [InlineData("real", CiFailureKind.Real)]
    [InlineData("unsure", CiFailureKind.Unsure)]
    public async Task ClassifyCi_ParsesTheThreeChoices(string choice, CiFailureKind expected)
    {
        var (client, _) = Create(_ => Answer(choice));

        Assert.Equal(expected, await client.ClassifyCiAsync("log", Ct));
    }

    [Theory]
    [InlineData("Flaky")]
    [InlineData("flaky ")]
    [InlineData("rerun everything")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task ClassifyCi_TreatsAnyOtherChoiceAsUnsure(string choice)
    {
        var (client, _) = Create(_ => Answer(choice));

        Assert.Equal(CiFailureKind.Unsure, await client.ClassifyCiAsync("log", Ct));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"answers":{}}""")]
    [InlineData("""{"answers":{"ci_failure":{"choice":42}}}""")]
    [InlineData("""{"answers":{"ci_failure":{}}}""")]
    public async Task ClassifyCi_TurnsAnUnreadableBodyIntoAJevFailure(string json)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(json));

        await Assert.ThrowsAsync<JevException>(() => client.ClassifyCiAsync("log", Ct));
    }

    [Fact]
    public async Task ClassifyCi_FailsOnAnErrorStatus()
    {
        var (client, _) = Create(_ => StubHttpHandler.Json("{}", HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<JevException>(() => client.ClassifyCiAsync("log", Ct));

        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyCi_SendsTheLogsAndOnlyTheCiQuestion()
    {
        var (client, handler) = Create(_ => Answer("flaky"));

        await client.ClassifyCiAsync("npm ERR! network timeout", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://litellm.example.test:4000/v1/systemone", request.Uri.ToString());
        Assert.Equal("Bearer sk-test-virtual", request.Authorization);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(new LiteLlmOptions().JevModel, (string?)body["model"]);
        var state = body["state"]!.AsObject();
        Assert.Equal(["ci_logs", "current_date"], state.Select(p => p.Key));
        Assert.Equal("npm ERR! network timeout", (string?)state["ci_logs"]);
        Assert.Equal("2026-10-12T07:05:09.123Z", (string?)state["current_date"]);
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["ci_failure"], questions.Select(q => q.Key));
        Assert.Equal("choice", (string?)questions["ci_failure"]!["type"]);
        Assert.Equal(["flaky", "real", "unsure"], questions["ci_failure"]!["criteria"]!.AsObject().Select(c => c.Key));
    }

    [Fact]
    public async Task ClassifyCi_PostsToTheConfiguredPathWithTheConfiguredModel()
    {
        var (client, handler) = Create(
            _ => Answer("flaky"),
            o =>
            {
                o.JevPath = "typesafe/v1/systemone";
                o.JevModel = "jev-2.0.0";
            });

        await client.ClassifyCiAsync("log", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://litellm.example.test:4000/typesafe/v1/systemone", request.Uri.ToString());
        Assert.Equal("jev-2.0.0", (string?)JsonNode.Parse(request.Body!)!["model"]);
    }

    [Fact]
    public async Task ClassifyCi_KeepsTheEndOfALongLog()
    {
        var (client, handler) = Create(_ => Answer("real"));
        var log = new string('a', JevClient.MaxCiLogChars) + "THE-FAILURE";

        await client.ClassifyCiAsync(log, Ct);

        var sent = (string)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["ci_logs"]!;
        Assert.Equal(JevClient.MaxCiLogChars, sent.Length);
        Assert.EndsWith("THE-FAILURE", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyCi_DoesNotSplitASurrogatePairWhenCutting()
    {
        var (client, handler) = Create(_ => Answer("real"));
        var log = "\U0001F600" + new string('a', JevClient.MaxCiLogChars - 1);

        await client.ClassifyCiAsync(log, Ct);

        var sent = (string)JsonNode.Parse(Assert.Single(handler.Requests).Body!)!["state"]!["ci_logs"]!;
        Assert.False(char.IsLowSurrogate(sent[0]));
    }

    [Fact]
    public async Task ClassifyCi_TurnsAnUnreachableServiceIntoAJevFailure()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<JevException>(() => client.ClassifyCiAsync("log", Ct));
    }
}
