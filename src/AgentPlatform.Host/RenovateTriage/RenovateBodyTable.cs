using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record RenovateUpdate(string Dependency, string? SourceRepo, string From, string To, string? SourcePath = null, string? Resolved = null);

public static partial class RenovateBodyTable
{
    public const int MaxRows = 50;

    public static IReadOnlyList<RenovateUpdate> Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        var lines = body.Split('\n').Select(l => l.Trim()).ToList();
        var header = lines.FindIndex(l => l.StartsWith('|') && Cells(l).Any(c => IsHeader(c, "Package")) && Cells(l).Any(c => IsHeader(c, "Change")));
        if (header < 0)
        {
            return [];
        }

        var cells = Cells(lines[header]);
        var packageColumn = cells.FindIndex(c => IsHeader(c, "Package"));
        var changeColumn = cells.FindIndex(c => IsHeader(c, "Change"));
        var updates = new List<RenovateUpdate>();
        foreach (var line in lines.Skip(header + 1).TakeWhile(l => l.StartsWith('|')))
        {
            var row = Cells(line);
            if (row.Count <= Math.Max(packageColumn, changeColumn) || ReadRow(row, packageColumn, changeColumn) is not { } update)
            {
                continue;
            }

            updates.Add(update);
            if (updates.Count == MaxRows)
            {
                break;
            }
        }

        return updates;
    }

    private static RenovateUpdate? ReadRow(List<string> row, int packageColumn, int changeColumn)
    {
        var packageCell = row[packageColumn];
        var change = Change().Match(row[changeColumn]);
        if (!change.Success)
        {
            return null;
        }

        var link = Link().Match(packageCell);
        var name = (link.Success ? link.Groups["text"].Value : packageCell).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var source = Source(packageCell);
        return new RenovateUpdate(
            name,
            source is null ? null : $"{source.Groups["owner"].Value}/{source.Groups["repo"].Value}",
            change.Groups["from"].Value,
            change.Groups["to"].Value,
            source?.Groups["path"].Value is { Length: > 0 } path ? path : null,
            Resolved(row));
    }

    internal static string Bare(string version) => version.TrimStart('=', 'v', 'V');

    private static string? Resolved(List<string> row)
    {
        var versions = Link().Matches(string.Join(' ', row))
            .Select(l => ResolvedVersion().Match(l.Groups["url"].Value))
            .Where(m => m.Success)
            .Select(m => Uri.UnescapeDataString(m.Groups["version"].Value))
            .DistinctBy(Bare, StringComparer.Ordinal)
            .ToList();
        return versions.Count == 1 ? versions[0] : null;
    }

    private static Match? Source(string packageCell)
    {
        var links = Link().Matches(packageCell).ToList();
        var link = links.Find(l => string.Equals(l.Groups["text"].Value, "source", StringComparison.OrdinalIgnoreCase))
            ?? links.Find(l => GitHubSource().IsMatch(l.Groups["url"].Value));
        var source = link is null ? Match.Empty : GitHubSource().Match(link.Groups["url"].Value);
        return source.Success ? source : null;
    }

    private static List<string> Cells(string line)
    {
        var cells = line.Split('|').Select(c => c.Trim()).ToList();
        if (cells.Count > 0 && cells[0].Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    private static bool IsHeader(string cell, string name) => string.Equals(cell, name, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Link();

    [GeneratedRegex(@"^https://(?:redirect\.)?github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)(?:/(?<path>.*?)/?)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GitHubSource();

    [GeneratedRegex(@"^https://(?:renovatebot\.com/diffs/(?:[^/?#]+/){3,}|developer\.mend\.io/api/mc/badges/(?:age|adoption)/(?:[^/?#]+/){2,})(?<version>[^/?#]+)(?:\?[^#]*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ResolvedVersion();

    [GeneratedRegex(@"`(?<from>[^`]+)`\s*(?:→|->)\s*`(?<to>[^`]+)`", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Change();
}
