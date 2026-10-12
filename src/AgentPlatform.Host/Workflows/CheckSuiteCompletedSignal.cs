using System.Text.Json.Serialization;

namespace AgentPlatform.Host.Workflows;

public sealed record CheckSuiteCompletedSignal(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("prNumber")] int PrNumber,
    [property: JsonPropertyName("headSha")] string HeadSha,
    [property: JsonPropertyName("checkSuiteId")] long CheckSuiteId,
    [property: JsonPropertyName("appSlug")] string? AppSlug,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("deliveryId")] string DeliveryId);
