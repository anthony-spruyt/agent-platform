using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.RunData;

public class JsonRedactorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("token")]
    [InlineData("sessionToken")]
    [InlineData("access_token")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("password")]
    [InlineData("client_secret")]
    [InlineData("credentials")]
    [InlineData("Authorization")]
    [InlineData("x-api-key")]
    [InlineData("apiKey")]
    [InlineData("private_key")]
    [InlineData("Cookie")]
    [InlineData("X-Hub-Signature-256")]
    public void Redact_ReplacesTheValueOfCredentialNamedProperties(string name)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "plain-value-123", ["safe"] = "kept" });

        var redacted = JsonDocument.Parse(JsonRedactor.Redact(Json(json))).RootElement;

        Assert.Equal("[redacted]", redacted.GetProperty(name).GetString());
        Assert.Equal("kept", redacted.GetProperty("safe").GetString());
    }

    [Fact]
    public void Redact_KeepsTokenCountMetrics()
    {
        var redacted = JsonRedactor.Redact(Json("""{"input_tokens":10,"outputTokens":20,"max_tokens":30}"""));

        Assert.Equal("""{"input_tokens":10,"outputTokens":20,"max_tokens":30}""", redacted);
    }

    [Fact]
    public void Redact_ReachesNestedObjectsAndArrays()
    {
        var redacted = JsonRedactor.Redact(Json("""{"headers":[{"name":"a","authorization":"Basic abc"}],"n":{"deep":{"password":"p"}}}"""));

        Assert.DoesNotContain("Basic abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"p\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"a\"", redacted, StringComparison.Ordinal);
    }

    public static TheoryData<string> CredentialShapes() =>
    [
        "ghs_" + new string('a', 36),
        "github_pat_" + new string('b', 40),
        "sk-ant-" + new string('c', 30),
        "sk-" + new string('d', 30),
        "Bearer " + new string('e', 32),
        "eyJ" + new string('f', 20) + "." + new string('g', 20) + "." + new string('h', 20),
        "AKIA" + new string('Q', 16),
        "v2." + new string('i', 12) + "." + new string('j', 8) + ".1893456000." + new string('k', 43),
    ];

    [Theory]
    [MemberData(nameof(CredentialShapes))]
    public void Redact_ScrubsCredentialShapedStringsAnywhere(string secret)
    {
        var redacted = JsonRedactor.Redact(Json(JsonSerializer.Serialize(new { text = $"before {secret} after", list = new[] { secret } })));

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("before [redacted] after", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ScrubsARealSessionToken()
    {
        var token = new AgentSessionTokens("0123456789abcdef0123456789abcdef").Issue("spike-1", "run-1", DateTimeOffset.UtcNow.AddHours(1));

        var redacted = JsonRedactor.Redact(Json(JsonSerializer.Serialize(new { detail = $"auth failed for {token}" })));

        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LeavesOrdinaryDataUntouched()
    {
        const string json = """{"repo":"owner/repo","pr":12,"verdict":"safe","confidence":0.9,"flag":true,"none":null,"list":[1,"two",{"k":"v"}]}""";

        Assert.Equal(json, JsonRedactor.Redact(Json(json)));
    }

    [Fact]
    public async Task Store_NeverPersistsCredentialMaterialInAnyJsonColumn()
    {
        var data = InMemoryRunData.Create();
        var secret = "ghs_" + new string('a', 36);
        var started = DateTimeOffset.UtcNow;

        var run = await data.Store.BeginRunAsync(
            "wf",
            "run",
            new BeginRunInput(
                "Test",
                Json($$"""{"headers":{"Authorization":"Bearer {{secret}}"},"body":"{{secret}}"}"""),
                started,
                Context: Json($$"""{"env":{"GITHUB_TOKEN":"{{secret}}"},"note":"{{secret}}"}""")),
            Ct);
        await data.Store.RecordStepAsync(
            new RecordStepInput(
                run.RunId, 1, "step", LadderRung.Rule, "done", started, 1,
                Input: Json($$"""{"api_key":"{{secret}}"}"""),
                Output: Json($$"""{"text":"{{secret}}"}""")),
            Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var row = await db.Runs.SingleAsync(Ct);
        var step = await db.RunSteps.SingleAsync(Ct);
        foreach (var stored in new[] { row.InputEvent, row.Context, step.Input, step.Output })
        {
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        }
    }
}
