using System.Text.Json;

namespace AgentPlatform.Host.Agents;

public sealed record SubmitResultPayload(string Status, string Summary, JsonElement? Data, string? Step = null, int? Attempt = null);

public static class AgentResultUpdates
{
    public const string SubmitResult = "submit_result";
    public const string ResultAlreadySubmittedErrorType = "ResultAlreadySubmitted";
    public const string InvalidResultErrorType = "InvalidResult";
    public const string StaleRunErrorType = "StaleRun";
}
