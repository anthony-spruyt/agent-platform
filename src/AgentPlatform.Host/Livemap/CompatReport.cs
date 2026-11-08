using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

public enum CompatResult
{
    Known,
    Easy,
    Hard,
}

public sealed partial record CompatReport(
    CompatResult Result,
    long TimeDateStamp,
    long SizeOfImage,
    string Sha256,
    string BaseLabel,
    string BaseSigset,
    string? MatchedRow,
    IReadOnlyList<string> Reasons)
{
    public static JsonObject? ParseObject(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryParseHex(string? text, out long value)
    {
        value = 0;
        return text is not null && Hex().IsMatch(text) && long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool TryParse(string reportJson, long buildId, string manifestGid, out CompatReport? report, out string problem)
    {
        report = null;
        problem = string.Empty;
        var root = ParseObject(reportJson);
        if (root is null)
        {
            problem = "The report is not a JSON object.";
            return false;
        }

        var parsed = Read(root, buildId, manifestGid);
        if (parsed.Report is null)
        {
            problem = parsed.Problem;
            return false;
        }

        report = parsed.Report;
        return true;
    }

    private static (CompatReport? Report, string Problem) Read(JsonObject root, long buildId, string manifestGid)
    {
        if (Text(root["result"]) switch { "known" => CompatResult.Known, "easy" => CompatResult.Easy, "hard" => CompatResult.Hard, _ => (CompatResult?)null } is not { } kind)
        {
            return (null, "The report has no known result.");
        }

        if (Text(root["build_id"]) != buildId.ToString(CultureInfo.InvariantCulture) || Text(root["manifest"]) != manifestGid)
        {
            return (null, "The report is about another build or manifest.");
        }

        var exe = root["exe"] as JsonObject;
        var baseRow = root["base"] as JsonObject;
        if (exe?["time_date_stamp"] is not JsonValue stampValue
            || !stampValue.TryGetValue<long>(out var stamp)
            || !TryParseHex(Text(exe["size_of_image"]), out var size)
            || Text(exe["sha256"]) is not { } sha
            || !Sha256Pattern().IsMatch(sha)
            || Text(baseRow?["label"]) is not { Length: > 0 } baseLabel
            || Text(baseRow?["sigset"]) is not { Length: > 0 } baseSigset)
        {
            return (null, "The report lacks the fingerprint of the executable or its base row.");
        }

        var reasons = (root["reasons"] as JsonArray)?.Select(Text).OfType<string>().ToList() ?? [];
        return (new CompatReport(kind, stamp, size, sha, baseLabel, baseSigset, Text(root["matched_row"]), reasons), string.Empty);
    }

    [GeneratedRegex(@"^0x[0-9a-fA-F]{1,15}\z")]
    private static partial Regex Hex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Pattern();
}
