using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public static partial class ChangelogSectionText
{
    public static string? Extract(string? changelog, string version, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        if (string.IsNullOrEmpty(changelog))
        {
            return null;
        }

        var bare = version.TrimStart('v', 'V');
        if (bare.Length == 0)
        {
            return null;
        }

        var wanted = new Regex($@"(?<![\w.])v?{Regex.Escape(bare)}(?!\w|[.-]\w)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = -1;
        var level = 0;
        var end = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = Heading().Match(lines[i]);
            if (!heading.Success)
            {
                continue;
            }

            var headingLevel = heading.Groups["hashes"].Length;
            if (start < 0)
            {
                if (wanted.IsMatch(heading.Groups["text"].Value))
                {
                    start = i;
                    level = headingLevel;
                }
            }
            else if (headingLevel <= level)
            {
                end = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var section = string.Join('\n', lines[start..end]).Trim();
        return section.Length <= maxChars ? section : section[..maxChars];
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();
}
