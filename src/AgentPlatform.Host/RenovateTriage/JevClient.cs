using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class JevException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IJevClient
{
    Task<JevAnswers> AskAsync(string? prBody, string? prDiff, CancellationToken cancellationToken);

    Task<CiFailureKind> ClassifyCiAsync(string ciLogs, CancellationToken cancellationToken);
}

public sealed class JevClient(HttpClient http, IOptions<LiteLlmOptions> options, TimeProvider time) : IJevClient
{
    public const string Model = "jev-latest";

    public const int MaxPrBodyBytes = 32 * 1024;

    public const int MaxPrDiffBytes = 32 * 1024;

    public const int MaxCiLogChars = 24_000;

    private const string QuestionsResource = "JevQuestions.json";
    private const string CiQuestionsResource = "JevCiQuestions.json";

    private static readonly Lazy<JsonNode> _questions = new(() => LoadQuestions(QuestionsResource));
    private static readonly Lazy<JsonNode> _ciQuestions = new(() => LoadQuestions(CiQuestionsResource));

    public Task<JevAnswers> AskAsync(string? prBody, string? prDiff, CancellationToken cancellationToken) =>
        SendAsync(
            new JsonObject
            {
                ["pr_body"] = Cap(prBody, MaxPrBodyBytes),
                ["pr_diff"] = Cap(prDiff, MaxPrDiffBytes),
                ["current_date"] = Now(),
            },
            _questions.Value,
            Parse,
            cancellationToken);

    public Task<CiFailureKind> ClassifyCiAsync(string ciLogs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciLogs);
        return SendAsync(
            new JsonObject
            {
                ["ci_logs"] = CapTail(ciLogs, MaxCiLogChars),
                ["current_date"] = Now(),
            },
            _ciQuestions.Value,
            ParseCi,
            cancellationToken);
    }

    private string Now() => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task<T> SendAsync<T>(JsonObject state, JsonNode questions, Func<string, T> parse, CancellationToken cancellationToken)
    {
        var litellm = options.Value;
        var body = new JsonObject
        {
            ["state"] = state,
            ["model"] = Model,
            ["questions"] = questions.DeepClone(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(litellm.BaseUrl, UriKind.Absolute), "typesafe/v1/systemone"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", litellm.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new JevException($"Jev answered with status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return parse(json);
        }
        catch (Exception ex) when (ex is not JevException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new JevException("Jev could not be reached or did not answer in time.", ex);
        }
    }

    private static string? Cap(string? text, int maxBytes)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string CapTail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var start = text.Length - maxChars;
        return char.IsLowSurrogate(text[start]) ? text[(start + 1)..] : text[start..];
    }

    private static CiFailureKind ParseCi(string json)
    {
        try
        {
            var choice = (string?)JsonNode.Parse(json)?["answers"]?["ci_failure"]?["choice"]
                ?? throw new JevException("Jev did not answer the CI question.");
            return choice switch
            {
                "flaky" => CiFailureKind.Flaky,
                "real" => CiFailureKind.Real,
                _ => CiFailureKind.Unsure,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new JevException("Jev returned an unreadable body.", ex);
        }
    }

    private static JevAnswers Parse(string json)
    {
        try
        {
            var answers = JsonNode.Parse(json)?["answers"];
            var breaking = Probability(answers?["breaking"]?["noul"]);
            var changelog = Probability(answers?["changelog"]?["noul"]);
            var bump = (string?)answers?["bump_type"]?["choice"];
            if (string.IsNullOrEmpty(bump))
            {
                throw new JevException("Jev did not answer the bump type.");
            }

            return new JevAnswers(breaking, bump, changelog);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new JevException("Jev returned an unreadable body.", ex);
        }
    }

    private static double Probability(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var probability) || probability is < 0 or > 1)
        {
            throw new JevException("Jev did not answer a probability question.");
        }

        return probability;
    }

    private static JsonNode LoadQuestions(string resource)
    {
        using var stream = typeof(JevClient).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The {resource} resource is missing.");
        return JsonNode.Parse(stream)!;
    }
}
