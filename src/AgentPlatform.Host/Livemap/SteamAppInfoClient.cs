using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPlatform.Host.Livemap;

public sealed record SteamBuild(long BuildId, string ManifestGid, DateTimeOffset UpdatedAt);

public sealed class SteamAppInfoException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface ISteamAppInfo
{
    Task<SteamBuild> GetPublicBuildAsync(CancellationToken cancellationToken);
}

public sealed class SteamAppInfoClient(HttpClient http) : ISteamAppInfo
{
    public const string AppId = "2278520";

    public const string DepotId = "2278521";

    public async Task<SteamBuild> GetPublicBuildAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"v1/info/{AppId}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SteamAppInfoException($"Steam app info answered with status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new SteamAppInfoException("Steam app info could not be read.", ex);
        }
    }

    private static SteamBuild Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || Text(root, "status") != "success")
        {
            throw new SteamAppInfoException("Steam app info did not report success.");
        }

        var branch = new[] { "data", AppId, "depots", "branches", "public" };
        if (!SteamIds.TryParseBuildId(Text(root, [.. branch, "buildid"]), out var buildId))
        {
            throw new SteamAppInfoException("Steam app info has no valid public build id.");
        }

        var gid = Text(root, "data", AppId, "depots", DepotId, "manifests", "public", "gid");
        if (gid is null || !SteamIds.IsManifestGid(gid))
        {
            throw new SteamAppInfoException("Steam app info has no valid public manifest id.");
        }

        var updated = Text(root, [.. branch, "timeupdated"]);
        if (updated is null
            || !long.TryParse(updated, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is <= 0 or > 253402300799)
        {
            throw new SteamAppInfoException("Steam app info has no valid update time.");
        }

        return new SteamBuild(buildId, gid, DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    private static string? Text(JsonObject root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var key in path)
        {
            node = node is JsonObject obj ? obj[key] : null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
