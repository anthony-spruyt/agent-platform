using System.Text.Json;

namespace AgentPlatform.Host.Agents;

public sealed class ClaudeLimitTracker(long maxTokens, int maxSubagents)
{
    private readonly Dictionary<string, long> _tokensByMessage = [];
    private readonly HashSet<string> _subagentCalls = [];

    public string? Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ObserveTokens(message) ?? ObserveSubagents(message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? ObserveTokens(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id) || id.GetString() is not { } messageId
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        _tokensByMessage[messageId] = Count(usage, "input_tokens") + Count(usage, "output_tokens") + Count(usage, "cache_creation_input_tokens");
        var total = _tokensByMessage.Values.Sum();
        return total > maxTokens ? $"Token ceiling of {maxTokens} exceeded ({total} tokens streamed)." : null;
    }

    private string? ObserveSubagents(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type) && type.GetString() == "tool_use"
                && block.TryGetProperty("name", out var name) && name.GetString() is "Task" or "Agent"
                && block.TryGetProperty("id", out var id) && id.GetString() is { } callId)
            {
                _subagentCalls.Add(callId);
            }
        }

        return _subagentCalls.Count > maxSubagents ? $"Subagent limit of {maxSubagents} exceeded." : null;
    }

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
