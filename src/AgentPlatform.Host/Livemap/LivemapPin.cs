using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

public static partial class LivemapPin
{
    private const string VersionKey = "LIVEMAP_VERSION:";

    private const string DigestKey = "LIVEMAP_SHA256:";

    public static string? ReadVersion(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var lines = pin.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(VersionKey, StringComparison.Ordinal)).ToList();
        return lines.Count == 1 && VersionLine().Match(lines[0]) is { Success: true } match ? match.Groups["tag"].Value : null;
    }

    // The two values are swapped on their own lines and nothing else changes, so the annotated Renovate manager keeps matching the file.
    public static string? Write(string pin, string tag, string sha256)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(sha256);

        if (!ReleaseTag().IsMatch(tag) || !Digest().IsMatch(sha256) || ReadVersion(pin) is null)
        {
            return null;
        }

        var lines = pin.Split('\n');
        var digestLines = lines.Count(line => line.TrimStart().StartsWith(DigestKey, StringComparison.Ordinal));
        if (digestLines != 1)
        {
            return null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (VersionValue().IsMatch(lines[i]))
            {
                lines[i] = VersionValue().Replace(lines[i], $"${{head}}{tag}${{tail}}");
            }
            else if (lines[i].TrimStart().StartsWith(DigestKey, StringComparison.Ordinal))
            {
                if (!DigestValue().IsMatch(lines[i]))
                {
                    return null;
                }

                lines[i] = DigestValue().Replace(lines[i], $"${{head}}{sha256}${{tail}}");
            }
        }

        return string.Join('\n', lines);
    }

    // Both tags are plain release tags, so comparing the three numbers is the whole semver check.
    public static bool IsAhead(string tag, string pinnedTag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(pinnedTag);

        return Numbers(tag).CompareTo(Numbers(pinnedTag)) > 0;
    }

    private static Version Numbers(string tag) =>
        ReleaseTag().IsMatch(tag) ? Version.Parse(tag[1..]) : throw new ArgumentException("The tag must be a plain release tag.", nameof(tag));

    [GeneratedRegex("^LIVEMAP_VERSION:[ \t]*\"(?<tag>v[0-9]+\\.[0-9]+\\.[0-9]+)\"$")]
    private static partial Regex VersionLine();

    [GeneratedRegex("^(?<head>[ \t]*LIVEMAP_VERSION:[ \t]*\")v[0-9]+\\.[0-9]+\\.[0-9]+(?<tail>\"[ \t\r]*)$")]
    private static partial Regex VersionValue();

    [GeneratedRegex("^(?<head>[ \t]*LIVEMAP_SHA256:[ \t]*\")[a-f0-9]{64}(?<tail>\"[ \t\r]*)$")]
    private static partial Regex DigestValue();

    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+\\z")]
    private static partial Regex ReleaseTag();

    [GeneratedRegex("^[a-f0-9]{64}\\z")]
    private static partial Regex Digest();
}
