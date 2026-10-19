using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record RenovateUpdate(string Dependency, string? SourceRepo, string From, string To);

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
            if (row.Count <= Math.Max(packageColumn, changeColumn) || ReadRow(row[packageColumn], row[changeColumn]) is not { } update)
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

    private static RenovateUpdate? ReadRow(string packageCell, string changeCell)
    {
        var change = Change().Match(changeCell);
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

        return new RenovateUpdate(
            name,
            SourceRepo(packageCell),
            change.Groups["from"].Value,
            change.Groups["to"].Value);
    }

    private static string? SourceRepo(string packageCell)
    {
        var links = Link().Matches(packageCell).ToList();
        var link = links.Find(l => string.Equals(l.Groups["text"].Value, "source", StringComparison.OrdinalIgnoreCase))
            ?? links.Find(l => GitHubSource().IsMatch(l.Groups["url"].Value));
        var source = link is null ? Match.Empty : GitHubSource().Match(link.Groups["url"].Value);
        return source.Success ? $"{source.Groups["owner"].Value}/{source.Groups["repo"].Value}" : null;
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

    [GeneratedRegex(@"^https://(?:redirect\.)?github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)(?:/.*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GitHubSource();

    [GeneratedRegex(@"`(?<from>[^`]+)`\s*(?:→|->)\s*`(?<to>[^`]+)`", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Change();
}
