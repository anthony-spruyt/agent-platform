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
    [InlineData("auth")]
    [InlineData("pwd")]
    [InlineData("pass")]
    [InlineData("connection string")]
    [InlineData("connectionString")]
    [InlineData("ConnectionString")]
    [InlineData("dsn")]
    [InlineData("session")]
    [InlineData("x-litellm-key")]
    [InlineData("PGPASSWORD")]
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

    [Theory]
    [InlineData("promptTokenCount")]
    [InlineData("token_count")]
    [InlineData("tokens_in")]
    [InlineData("tokens_out")]
    [InlineData("tokenizer")]
    [InlineData("secretName")]
    [InlineData("secret_name")]
    [InlineData("author")]
    [InlineData("passed")]
    [InlineData("sessions_total")]
    public void Redact_KeepsNamesThatAreNotCredentials(string name)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "plain-value" });

        Assert.Equal(json, JsonRedactor.Redact(Json(json)));
    }

    [Fact]
    public void Redact_KeepsEveryTokenCountFieldTheStepsAndSchemaUse()
    {
        const string json = """{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":3,"cache_creation_input_tokens":4,"inputTokens":1,"outputTokens":2,"max_tokens":30,"total_tokens":5,"promptTokenCount":6,"tokens_in":7,"tokens_out":8}""";

        Assert.Equal(json, JsonRedactor.Redact(Json(json)));
    }

    [Fact]
    public void Redact_ReplacesATokensArray()
    {
        var redacted = JsonRedactor.Redact(Json("""{"tokens":["abc-123-secret-value","def"],"kept":1}"""));

        Assert.Equal("""{"tokens":"[redacted]","kept":1}""", redacted);
    }

    [Fact]
    public void Redact_ReplacesAnEnvStyleValueWhenTheNameIsSensitive()
    {
        var redacted = JsonRedactor.Redact(Json("""{"env":[{"name":"PGPASSWORD","value":"hunter2-hunter2"},{"name":"REGION","value":"eu-west-1"}]}"""));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"eu-west-1\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"PGPASSWORD\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ReplacesAnEnvStyleKeyValuePair()
    {
        var redacted = JsonRedactor.Redact(Json("""{"key":"api_key","value":"hunter2-hunter2"}"""));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ScrubsCredentialShapedPropertyNames()
    {
        var secret = "ghs_" + new string('a', 36);

        var redacted = JsonRedactor.Redact(Json($$"""{"{{secret}}":"value"}"""));

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RedactsJsonNestedInsideAString()
    {
        var inner = JsonSerializer.Serialize(new { result = new { password = "hunter2-hunter2", note = "ok" }, list = new[] { new { api_key = "k-12345678" } } });
        var redacted = JsonRedactor.Redact(Json(JsonSerializer.Serialize(new { ResultText = inner })));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("k-12345678", redacted, StringComparison.Ordinal);
        Assert.Contains("ok", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RedactsJsonArrayNestedInsideAString()
    {
        var redacted = JsonRedactor.Redact(Json("""{"text":"[{\"secret\":\"hunter2-hunter2\"}]"}"""));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LeavesStringsThatOnlyLookLikeJsonAlone()
    {
        const string json = """{"a":"{not json","b":"[1, 2","c":"[redacted]"}""";

        Assert.Equal(json, JsonRedactor.Redact(Json(json)));
    }

    [Fact]
    public void Redact_RedactsJsonNestedInStringsBeyondTheDepthLimit()
    {
        var text = "{\"password\":\"hunter2-hunter2\"}";
        for (var i = 0; i < 12; i++)
        {
            text = JsonSerializer.Serialize(new { inner = text });
        }

        var redacted = JsonRedactor.Redact(Json(JsonSerializer.Serialize(new { text })));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_StripsNulCharactersFromValuesAndNames()
    {
        var redacted = JsonRedactor.Redact(Json("""{"na\u0000me":"a\u0000b"}"""));

        Assert.Equal("""{"name":"ab"}""", redacted);
        Assert.DoesNotContain("\\u0000", redacted, StringComparison.Ordinal);
    }

    public static TheoryData<string> TextCredentials() =>
    [
        "bearer " + new string('e', 32),
        "BEARER " + new string('e', 32),
        "Bearer%20" + new string('e', 32),
        "bearer%20" + new string('e', 32),
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA1234567890abcdef\nZm9vYmFy\n-----END RSA PRIVATE KEY-----",
        "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----",
        "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmU",
        "sl_" + new string('x', 20),
        "Server=db;Database=app;User Id=u;Password=hunter2-hunter2;Timeout=5",
        "Server=db;Database=app;User Id=u;pwd=hunter2-hunter2;Timeout=5",
        "Host=db;PASSWORD=hunter2-hunter2",
        "Host=db;Password=\"hunter 2 hunter2\";Port=5432",
        "postgres://app:hunter2-hunter2@db.internal:5432/app",
        "https://user:p%40ss-hunter2@example.test/path",
        "postgres://app:hunter2@hunter2@db.internal/app",
        "PGPASSWORD=hunter2-hunter2 psql",
    ];

    [Theory]
    [MemberData(nameof(TextCredentials))]
    public void RedactText_ScrubsCredentialMaterial(string text)
    {
        var redacted = JsonRedactor.RedactText($"before {text} after");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eeeeeeee", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxxxxx", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("XWZPtnDK", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("PNVJaVVP", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("b3BlbnNz", redacted, StringComparison.Ordinal);
        Assert.StartsWith("before ", redacted, StringComparison.Ordinal);
        Assert.Contains(JsonRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TextCredentials))]
    public void Redact_ScrubsTextCredentialMaterialInsideJsonStrings(string text)
    {
        var redacted = JsonRedactor.Redact(Json(JsonSerializer.Serialize(new { detail = $"before {text} after" })));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eeeeeeee", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxxxxx", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("XWZPtnDK", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("PNVJaVVP", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("b3BlbnNz", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("The basic idea is simple")]
    [InlineData("see https://example.test/a/b?x=1 for details")]
    [InlineData("the bearer of bad news")]
    [InlineData("ssh://git@example.test/repo.git")]
    [InlineData("sl_short")]
    [InlineData("a passphrase-free flow with password rotation")]
    public void RedactText_LeavesOrdinaryTextAlone(string text)
    {
        Assert.Equal(text, JsonRedactor.RedactText(text));
    }

    [Fact]
    public void RedactText_StripsNulCharacters()
    {
        Assert.Equal("ab", JsonRedactor.RedactText("a\u0000b"));
    }

    [Fact]
    public void RedactText_RedactsAJsonDocumentHeldInAString()
    {
        var redacted = JsonRedactor.RedactText("""{"password":"hunter2-hunter2","ok":1}""");

        Assert.Equal("""{"password":"[redacted]","ok":1}""", redacted);
    }

    private static readonly string[] StoredTextColumns = ["WorkflowId", "WorkflowRunId", "WorkflowType", "Model", "InputEvent", "Context", "Name", "StopReason", "Input", "Output", "Body"];

    private static readonly string[] NonCredentialColumns = ["Hash", "PromptHash", "Outcome", "OutcomeDetail"];

    [Fact]
    public async Task Store_NeverPersistsCredentialMaterialInAnyTextColumn()
    {
        var data = InMemoryRunData.Create();
        var secret = "ghs_" + new string('a', 36);
        var started = DateTimeOffset.UtcNow;

        var run = await data.Store.BeginRunAsync(
            $"wf-{secret}",
            $"run-{secret}",
            new BeginRunInput(
                $"Test {secret}",
                Json($$"""{"headers":{"Authorization":"Bearer {{secret}}"},"body":"{{secret}}"}"""),
                started,
                Context: Json($$"""{"env":{"GITHUB_TOKEN":"{{secret}}"},"note":"{{secret}}"}"""),
                PromptName: $"prompt {secret}",
                PromptBody: $"Use {secret} and Password=hunter2-hunter2 to log in",
                Model: $"model-{secret}"),
            Ct);
        await data.Store.RecordStepAsync(
            new RecordStepInput(
                run.RunId, 1, $"step-{secret}", LadderRung.Rule, $"done-{secret}", started, 1,
                Input: Json($$"""{"api_key":"{{secret}}"}"""),
                Output: Json($$"""{"text":"{{secret}}"}"""),
                PromptHash: run.PromptHash,
                Model: $"model-{secret}"),
            Ct);
        await data.Store.CompleteRunAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, started.AddSeconds(1), $"final-{secret}"), Ct);

        await using var db = await data.Factory.CreateDbContextAsync(Ct);
        var stored = new List<(string Column, string? Value)>();
        foreach (var entity in new object[] { await db.Runs.SingleAsync(Ct), await db.RunSteps.SingleAsync(Ct), await db.Prompts.SingleAsync(Ct) })
        {
            foreach (var property in entity.GetType().GetProperties().Where(p => p.PropertyType == typeof(string)))
            {
                stored.Add((entity.GetType().Name + "." + property.Name, (string?)property.GetValue(entity)));
            }
        }

        Assert.NotEmpty(stored);
        foreach (var (column, value) in stored)
        {
            var name = column[(column.IndexOf('.', StringComparison.Ordinal) + 1)..];
            Assert.True(
                StoredTextColumns.Contains(name, StringComparer.Ordinal) || NonCredentialColumns.Contains(name, StringComparer.Ordinal),
                $"{column} is a text column this test does not cover.");
            Assert.False(value?.Contains(secret, StringComparison.Ordinal) ?? false, $"{column} holds credential material.");
            Assert.False(value?.Contains("hunter2", StringComparison.Ordinal) ?? false, $"{column} holds credential material.");
        }

        Assert.Equal(PromptHashing.Compute((await db.Prompts.SingleAsync(Ct)).Body), run.PromptHash);
    }
}
