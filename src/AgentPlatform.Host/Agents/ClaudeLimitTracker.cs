using System.Text.Json;

namespace AgentPlatform.Host.Agents;

public sealed class ClaudeLimitTracker(long maxTokens, int maxSubagents)
{
    private readonly long _ceiling = maxTokens >= long.MaxValue ? long.MaxValue : Math.Max(maxTokens, 0) + 1;
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
                || !IsString(root, "type", "assistant")
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
        if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { } messageId
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var count = Add(Add(Count(usage, "input_tokens"), Count(usage, "output_tokens")), Count(usage, "cache_creation_input_tokens"));
        _tokensByMessage[messageId] = Math.Max(count, _tokensByMessage.GetValueOrDefault(messageId));
        var total = 0L;
        foreach (var messageTokens in _tokensByMessage.Values)
        {
            total = Add(total, messageTokens);
        }

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
                && IsString(block, "type", "tool_use")
                && (IsString(block, "name", "Task") || IsString(block, "name", "Agent"))
                && block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } callId)
            {
                _subagentCalls.Add(callId);
            }
        }

        return _subagentCalls.Count > maxSubagents ? $"Subagent limit of {maxSubagents} exceeded." : null;
    }

    private static bool IsString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private long Add(long total, long count) => count >= _ceiling - total ? _ceiling : total + count;

    private long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? Math.Clamp(number, 0, _ceiling)
            : 0;
}
