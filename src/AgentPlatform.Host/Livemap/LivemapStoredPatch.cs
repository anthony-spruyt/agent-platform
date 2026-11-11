using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPlatform.Host.Livemap;

// The patch a build waits to ship, as kept in livemap_builds.patch_result. The status is text so the enum can be reordered.
public static class LivemapStoredPatch
{
    private const string DataStatus = "data";
    private const string CodeStatus = "code";

    public static string Write(LivemapResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is not (LivemapResultStatus.Data or LivemapResultStatus.Code) || result.BuildsJson is not { } buildsJson)
        {
            throw new ArgumentException("Only a data or code result with a builds file is stored.", nameof(result));
        }

        var status = result.Status == LivemapResultStatus.Data ? DataStatus : CodeStatus;
        return JsonSerializer.Serialize(new Stored(status, result.Summary, buildsJson, [.. result.Files.Select(f => new StoredFile(f.Path, f.Content))]));
    }

    public static LivemapResult? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var status = stored?.Status switch
        {
            DataStatus => LivemapResultStatus.Data,
            CodeStatus => LivemapResultStatus.Code,
            _ => (LivemapResultStatus?)null,
        };
        return status is { } known
            && stored?.Summary is { } summary
            && stored.BuildsJson is { } buildsJson
            && stored.Files is { } files
            && files.All(f => f.Path is not null && f.Content is not null)
                ? new LivemapResult(known, summary, buildsJson, [.. files.Select(f => new LivemapFile(f.Path!, f.Content!))], null)
                : null;
    }

    private sealed record Stored(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("builds_json")] string? BuildsJson,
        [property: JsonPropertyName("files")] List<StoredFile>? Files);

    private sealed record StoredFile(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("content")] string? Content);
}
