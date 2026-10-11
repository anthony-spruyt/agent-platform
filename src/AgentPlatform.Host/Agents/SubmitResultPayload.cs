using System.Text.Json;

namespace AgentPlatform.Host.Agents;

public sealed record SubmitResultPayload(string Status, string Summary, JsonElement? Data);
