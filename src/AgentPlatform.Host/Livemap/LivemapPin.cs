using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

public static partial class LivemapPin
{
    private const string VersionKey = "LIVEMAP_VERSION:";

    public static string? ReadVersion(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var lines = pin.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(VersionKey, StringComparison.Ordinal)).ToList();
        return lines.Count == 1 && VersionLine().Match(lines[0]) is { Success: true } match ? match.Groups["tag"].Value : null;
    }

    [GeneratedRegex("^LIVEMAP_VERSION:[ \t]*\"(?<tag>v[0-9]+\\.[0-9]+\\.[0-9]+)\"$")]
    private static partial Regex VersionLine();
}
