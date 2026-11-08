using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

public static partial class SteamIds
{
    public static bool TryParseBuildId(string? text, out long buildId)
    {
        buildId = 0;
        return text is not null && BuildIdPattern().IsMatch(text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out buildId);
    }

    public static bool IsManifestGid(string? text) => text is not null && GidPattern().IsMatch(text);

    [GeneratedRegex(@"^[1-9][0-9]{0,11}\z")]
    private static partial Regex BuildIdPattern();

    [GeneratedRegex(@"^[1-9][0-9]{0,19}\z")]
    private static partial Regex GidPattern();
}
