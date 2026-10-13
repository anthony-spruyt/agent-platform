using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Agents;

public sealed record ClaudeOutputSummary(
    bool HasResultEvent,
    string? Subtype,
    string? ResultText,
    bool StopReasonIsNull,
    long? OutputTokens,
    bool UsageLimitReached,
    string? UsageLimitMessage,
    AgentRunUsage? Usage,
    string? LimitBreach = null)
{
    public bool IsTruncated =>
        !HasResultEvent
        || Subtype != "success"
        || OutputTokens == 0
        || ResultText is "";
}

public static partial class ClaudeOutputAnalyzer
{
    public const int MaxResultTextLength = 16 * 1024;

    private const string RateLimitErrorValue = "rate_limit";
    private const string RateLimitFallbackMessage = "Claude rate limit reached";

    [GeneratedRegex(
        @"usage limit reached|hit your (usage |session |weekly )?limit|(5-hour|weekly|session|opus) limit reached|limit reached\s*[|∙·]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex UsageLimitText();

    public static Task<ClaudeOutputSummary> AnalyzeAsync(TextReader reader, CancellationToken cancellationToken) =>
        AnalyzeAsync(reader, null, cancellationToken);

    public static async Task<ClaudeOutputSummary> AnalyzeAsync(TextReader reader, Action<string>? observe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        ClaudeOutputSummary result = new(false, null, null, false, null, false, null, null);
        string? limitMessage = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            observe?.Invoke(line);
            if (!TryParseEvent(line, out var document))
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                limitMessage ??= FindUsageLimit(root);
                if (GetString(root, "type") == "result")
                {
                    result = ReadResult(root);
                }
            }
        }

        return result with { UsageLimitReached = limitMessage is not null, UsageLimitMessage = limitMessage };
    }

    private static bool TryParseEvent(string line, out JsonDocument document)
    {
        document = null!;
        if (!line.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        document.Dispose();
        return false;
    }

    private static ClaudeOutputSummary ReadResult(JsonElement root)
    {
        var text = GetString(root, "result");
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        var inputTokens = usage is { } iu ? GetLong(iu, "input_tokens") : null;
        var outputTokens = usage is { } ou ? GetLong(ou, "output_tokens") : null;
        var stopReasonIsNull = root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.Null;

        var clamped = text is { Length: > MaxResultTextLength } ? text[..MaxResultTextLength] : text;
        var summaryUsage = new AgentRunUsage(
            inputTokens,
            outputTokens,
            GetDouble(root, "total_cost_usd"),
            GetLong(root, "duration_ms"),
            (int?)GetLong(root, "num_turns"),
            GetString(root, "session_id"));

        return new ClaudeOutputSummary(true, GetString(root, "subtype"), clamped, stopReasonIsNull, outputTokens, false, null, summaryUsage);
    }

    private static string? FindUsageLimit(JsonElement root)
    {
        var type = GetString(root, "type");
        if (type == "rate_limit_event")
        {
            var status = root.TryGetProperty("rate_limit_info", out var info) ? GetString(info, "status") : null;
            return status == "rejected" ? "Claude rate limit rejected the request" : null;
        }

        if (type == "assistant" && GetString(root, "error") == RateLimitErrorValue)
        {
            return FirstMessageText(root) ?? RateLimitFallbackMessage;
        }

        var text = type switch
        {
            "result" when GetBool(root, "is_error") => GetString(root, "result"),
            "assistant" when GetBool(root, "isApiErrorMessage") => FirstMessageText(root),
            _ => null,
        };

        return text is not null && UsageLimitText().IsMatch(text) ? text : null;
    }

    private static string? FirstMessageText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "text") is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
