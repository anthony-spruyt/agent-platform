using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

public sealed class ShippedBuildsException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IShippedBuilds
{
    Task<IReadOnlySet<long>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class ShippedBuildsReader(IGitHubApiClient github, IOptions<LivemapOptions> options) : IShippedBuilds
{
    private const string BuildsPath = "data/builds.json";
    private const int MaxPinChars = 16 * 1024;
    private const int MaxBuildsChars = 1024 * 1024;

    public async Task<IReadOnlySet<long>> ReadAsync(CancellationToken cancellationToken)
    {
        var livemap = options.Value;

        var branch = await github.GetDefaultBranchAsync(livemap.PinRepo, cancellationToken).ConfigureAwait(false);
        var pin = await github.GetFileContentAsync(livemap.PinRepo, livemap.PinRepo, livemap.PinPath, branch, MaxPinChars, cancellationToken).ConfigureAwait(false)
            ?? throw new ShippedBuildsException("The Livemap pin file does not exist.");
        var tag = LivemapPin.ReadVersion(pin)
            ?? throw new ShippedBuildsException("The Livemap pin file has no readable version.");

        var builds = await github.GetFileContentAsync(livemap.Repo, livemap.Repo, BuildsPath, tag, MaxBuildsChars, cancellationToken).ConfigureAwait(false)
            ?? throw new ShippedBuildsException("The pinned Livemap release has no build list.");
        return ParseBuildIds(builds);
    }

    private static HashSet<long> ParseBuildIds(string json)
    {
        try
        {
            var shipped = new HashSet<long>();
            var rows = (JsonNode.Parse(json) as JsonObject)?["builds"] as JsonArray
                ?? throw new ShippedBuildsException("The Livemap build list has no builds.");
            foreach (var row in rows)
            {
                var ids = (row as JsonObject)?["buildids"] as JsonArray
                    ?? throw new ShippedBuildsException("A Livemap build row has no build ids.");
                foreach (var id in ids)
                {
                    if (!SteamIds.TryParseBuildId(id is JsonValue value && value.TryGetValue<string>(out var text) ? text : null, out var buildId))
                    {
                        throw new ShippedBuildsException("A Livemap build row has an invalid build id.");
                    }

                    shipped.Add(buildId);
                }
            }

            return shipped;
        }
        catch (JsonException ex)
        {
            throw new ShippedBuildsException("The Livemap build list is not valid JSON.", ex);
        }
    }
}
