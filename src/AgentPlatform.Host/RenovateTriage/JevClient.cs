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
    Task<JevAnswers> AskAsync(string? prBody, CancellationToken cancellationToken);
}

public sealed class JevClient(HttpClient http, IOptions<LiteLlmOptions> options, TimeProvider time) : IJevClient
{
    public const string Model = "jev-latest";

    public const int MaxPrBodyBytes = 32 * 1024;

    private const string QuestionsResource = "JevQuestions.json";

    private static readonly Lazy<JsonNode> _questions = new(LoadQuestions);

    public async Task<JevAnswers> AskAsync(string? prBody, CancellationToken cancellationToken)
    {
        var litellm = options.Value;
        var body = new JsonObject
        {
            ["state"] = new JsonObject
            {
                ["pr_body"] = CapBody(prBody),
                ["current_date"] = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            },
            ["model"] = Model,
            ["questions"] = _questions.Value.DeepClone(),
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
            return Parse(json);
        }
        catch (Exception ex) when (ex is not JevException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new JevException("Jev could not be reached or did not answer in time.", ex);
        }
    }

    private static string? CapBody(string? prBody)
    {
        if (prBody is null || Encoding.UTF8.GetByteCount(prBody) <= MaxPrBodyBytes)
        {
            return prBody;
        }

        var bytes = Encoding.UTF8.GetBytes(prBody);
        var length = MaxPrBodyBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
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

    private static JsonNode LoadQuestions()
    {
        using var stream = typeof(JevClient).Assembly.GetManifestResourceStream(QuestionsResource)
            ?? throw new InvalidOperationException($"The {QuestionsResource} resource is missing.");
        return JsonNode.Parse(stream)!;
    }
}
