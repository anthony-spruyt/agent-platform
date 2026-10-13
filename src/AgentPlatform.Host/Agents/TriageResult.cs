using System.Text.Json;

namespace AgentPlatform.Host.Agents;

public sealed record TriageVersion(string Dep, string From, string To);

public sealed record TriageResult(
    string Verdict,
    string HeadSha,
    string Summary,
    IReadOnlyList<TriageVersion> Versions,
    IReadOnlyList<string> Fix,
    IReadOnlyList<string> Reasons)
{
    public const int MaxSummaryLength = 4000;
    public const int MaxVersions = 50;
    public const int MaxDepLength = 200;
    public const int MaxVersionLength = 100;
    public const int MaxListItems = 20;
    public const int MaxItemLength = 1000;

    private static readonly string[] _verdicts = ["SAFE", "FIXABLE", "BLOCKED", "NEEDS_REVIEW"];

    public static bool TryParse(SubmitResultPayload payload, string expectedHeadSha, out TriageResult? result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        result = null;
        error = Check(payload, expectedHeadSha, out var parsed);
        if (error is not null)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static string? Check(SubmitResultPayload payload, string expectedHeadSha, out TriageResult? parsed)
    {
        parsed = null;
        if (payload.Status != "success")
        {
            return "status must be \"success\" for a triage result.";
        }

        if (string.IsNullOrWhiteSpace(payload.Summary) || payload.Summary.Length > MaxSummaryLength)
        {
            return $"summary is required and must be at most {MaxSummaryLength} characters.";
        }

        if (payload.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return "data must be a JSON object with verdict, head_sha, versions, fix and reasons.";
        }

        if (!data.TryGetProperty("verdict", out var verdictElement) || verdictElement.ValueKind != JsonValueKind.String
            || !_verdicts.Contains(verdictElement.GetString(), StringComparer.Ordinal))
        {
            return $"data.verdict must be one of {string.Join(", ", _verdicts)}.";
        }

        if (!data.TryGetProperty("head_sha", out var shaElement) || shaElement.ValueKind != JsonValueKind.String
            || !string.Equals(shaElement.GetString(), expectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return "data.head_sha must echo the head sha you were given.";
        }

        var versions = new List<TriageVersion>();
        if (Property(data, "versions", out var versionsElement))
        {
            if (versionsElement.ValueKind != JsonValueKind.Array || versionsElement.GetArrayLength() > MaxVersions)
            {
                return $"data.versions must be an array of at most {MaxVersions} {{dep, from, to}} objects.";
            }

            foreach (var item in versionsElement.EnumerateArray())
            {
                var version = ReadVersion(item);
                if (version is null)
                {
                    return $"each data.versions entry needs string dep (at most {MaxDepLength} characters), from and to (at most {MaxVersionLength} characters each).";
                }

                versions.Add(version);
            }
        }

        if (!ReadStrings(data, "fix", out var fix) || !ReadStrings(data, "reasons", out var reasons))
        {
            return $"data.fix and data.reasons must be arrays of at most {MaxListItems} strings of at most {MaxItemLength} characters.";
        }

        parsed = new TriageResult(verdictElement.GetString()!, shaElement.GetString()!, payload.Summary, versions, fix, reasons);
        return null;
    }

    private static bool Property(JsonElement data, string name, out JsonElement value) =>
        data.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static TriageVersion? ReadVersion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || Text(item, "dep", MaxDepLength) is not { } dep
            || Text(item, "from", MaxVersionLength) is not { } from
            || Text(item, "to", MaxVersionLength) is not { } to)
        {
            return null;
        }

        return new TriageVersion(dep, from, to);
    }

    private static string? Text(JsonElement item, string name, int max) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text && text.Length <= max
            ? text
            : null;

    private static bool ReadStrings(JsonElement data, string name, out List<string> values)
    {
        values = [];
        if (!Property(data, name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxListItems)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > MaxItemLength)
            {
                return false;
            }

            values.Add(text);
        }

        return true;
    }
}
