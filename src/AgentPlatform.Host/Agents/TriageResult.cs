using System.Globalization;
using System.Text.Json;
using AgentPlatform.Host.PullRequests;

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

    public static bool TryParse(SubmitResultPayload payload, string expectedHeadSha, StatedVersions? stated, out TriageResult? result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        result = null;
        error = Check(payload, expectedHeadSha, out var parsed) ?? (stated is null ? null : CheckVersions(parsed!.Versions, stated));
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

    private static string? CheckVersions(IReadOnlyList<TriageVersion> versions, StatedVersions stated)
    {
        if (stated.Rows is { } rows)
        {
            var expected = rows.Select(r => Key(r.Package, r.From, r.To)).Order(StringComparer.Ordinal);
            var actual = versions.Select(v => Key(v.Dep, v.From, v.To)).Order(StringComparer.Ordinal);
            return expected.SequenceEqual(actual, StringComparer.Ordinal) ? null : TableMismatch(rows);
        }

        var allowed = versions.Count switch
        {
            0 => true,
            1 => stated.SubjectVersion is { } subject && StripV(versions[0].To) == StripV(subject),
            _ => false,
        };
        return allowed
            ? null
            : "data.versions must be empty or hold one entry whose \"to\" is the version the commit subject states"
                + (stated.SubjectVersion is { } version ? $" ({version})." : ", and it states none.");
    }

    private static string TableMismatch(IReadOnlyList<StatedVersion> rows)
    {
        if (rows.Count == 0)
        {
            return "data.versions must be empty: the head commit's table lists no updates.";
        }

        var listed = string.Join("; ", rows.Take(MaxListItems).Select(r => $"{r.Package} {r.From} -> {r.To}"));
        var more = rows.Count > MaxListItems ? $" (and {rows.Count - MaxListItems} more)" : string.Empty;
        return $"data.versions must list exactly the updates in the head commit's table: {listed}{more}.";
    }

    private static string Key(string dep, string from, string to) =>
        string.Create(CultureInfo.InvariantCulture, $"{dep.ToLowerInvariant()}\n{StripV(from)}\n{StripV(to)}");

    private static string StripV(string version) => version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;

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
