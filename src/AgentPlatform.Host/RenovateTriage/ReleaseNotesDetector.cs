using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public static partial class ReleaseNotesDetector
{
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static bool Found(string? body, IReadOnlyList<string>? sections) =>
        Guarded(() => BodyCoversEveryUpdate(body) || SectionsCoverEveryUpdate(body, sections));

    public static bool BodyHasNotes(string? body) => Guarded(() => BodyCoversEveryUpdate(body));

    public static bool SectionHasNotes(string? section) => Guarded(() => SectionHasListItem(section));

    internal static bool Guarded(Func<bool> scan)
    {
        try
        {
            return scan();
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool BodyCoversEveryUpdate(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var updates = RenovateBodyTable.Parse(body);
        if (updates.Count == 0)
        {
            return false;
        }

        var blocks = Blocks(Clean(body), updates);
        return updates.All(update => blocks.Exists(block => block.Covers(update)));
    }

    private static bool SectionsCoverEveryUpdate(string? body, IReadOnlyList<string>? sections)
    {
        if (sections is not { Count: > 0 })
        {
            return false;
        }

        var updates = RenovateBodyTable.Parse(body);
        if (!updates.All(ChangelogFetcher.IsFetchable))
        {
            return false;
        }

        var fetchable = ChangelogFetcher.FetchableUpdates(updates).Count;
        return fetchable <= ChangelogFetcher.MaxDependencies && sections.Count == fetchable && sections.All(SectionHasListItem);
    }

    private static bool SectionHasListItem(string? section) =>
        !string.IsNullOrWhiteSpace(section) && HasListItemWithText(Clean(section));

    private static List<Block> Blocks(List<Line> lines, IReadOnlyList<RenovateUpdate> updates)
    {
        var blocks = new List<Block>();
        var heading = lines.FindIndex(l => ReleaseNotesHeading().IsMatch(l.Text));
        if (heading < 0)
        {
            return blocks;
        }

        Summary? open = null;
        var content = new List<Line>();
        var depth = 0;
        for (var i = heading + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Fenced && StartOf(lines, i, updates) is { } started)
            {
                (open, content, depth) = (started, [], 1);
                continue;
            }

            if (open is null)
            {
                if (ConfigurationHeading().IsMatch(line.Text))
                {
                    break;
                }

                continue;
            }

            if (!line.Fenced && DetailsOpen().IsMatch(line.Text))
            {
                depth++;
            }
            else if (!line.Fenced && DetailsClose().IsMatch(line.Text) && (depth > 1 || EndsBlock(lines, i)))
            {
                depth--;
                if (depth == 0)
                {
                    blocks.Add(Block.Of(open, content));
                    open = null;
                    continue;
                }
            }

            content.Add(line);
        }

        return blocks;
    }

    private static Summary? StartOf(List<Line> lines, int index, IReadOnlyList<RenovateUpdate> updates)
    {
        if (!DetailsOpen().IsMatch(lines[index].Text))
        {
            return null;
        }

        var next = lines.FindIndex(index + 1, l => l.Text.Trim().Length > 0);
        if (next < 0)
        {
            return null;
        }

        var title = SummaryLine().Match(lines[next].Text).Groups["title"].Value;
        return Summary.Parse(title) is { } summary && updates.Any(summary.Matches) ? summary : null;
    }

    private static bool EndsBlock(List<Line> lines, int closing)
    {
        var next = lines.FindIndex(closing + 1, l => l.Text.Trim().Length > 0);
        return next < 0 || DetailsOpen().IsMatch(lines[next].Text) || ThematicBreak().IsMatch(lines[next].Text);
    }

    private static List<Line> Clean(string text)
    {
        var lines = new List<Line>();
        var fenced = false;
        var commented = false;
        foreach (var raw in text.Split('\n'))
        {
            if (!commented)
            {
                var marker = Fence().IsMatch(raw);
                if (marker || fenced)
                {
                    fenced ^= marker;
                    lines.Add(new Line(raw, Fenced: true));
                    continue;
                }
            }

            lines.Add(new Line(WithoutComments(raw, ref commented), Fenced: false));
        }

        return lines;
    }

    private static string WithoutComments(string line, ref bool commented)
    {
        var visible = new StringBuilder();
        var at = 0;
        while (at < line.Length)
        {
            if (commented)
            {
                var close = line.IndexOf("-->", at, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                commented = false;
                at = close + "-->".Length;
            }
            else
            {
                var open = line.IndexOf("<!--", at, StringComparison.Ordinal);
                if (open < 0)
                {
                    visible.Append(line, at, line.Length - at);
                    break;
                }

                visible.Append(line, at, open - at);
                commented = true;
                at = open + "<!--".Length;
            }
        }

        return visible.ToString();
    }

    private static bool HasListItemWithText(IEnumerable<Line> lines) =>
        lines.Any(l => !l.Fenced && ListItem().Match(l.Text) is { Success: true } item && HasText(item.Groups["text"].Value));

    private static bool IsVersionHeading(Line line) =>
        !line.Fenced
        && VersionHeading().Match(line.Text) is { Success: true } heading
        && VersionText().IsMatch(Unquoted(heading.Groups["text"].Value));

    private static string VersionOf(Line heading) =>
        Bare(Unquoted(VersionHeading().Match(heading.Text).Groups["text"].Value));

    private static string Bare(string version) => version.TrimStart('=', 'v', 'V');

    private static string Unquoted(string text) => text.Replace("`", string.Empty, StringComparison.Ordinal).Trim();

    private static bool HasText(string item) => LetterOrDigit().IsMatch(NonText().Replace(item, " "));

    private static string Normalised(string text) => WebUtility.HtmlDecode(text).Replace("\u200B", string.Empty, StringComparison.Ordinal);

    private readonly record struct Line(string Text, bool Fenced);

    private sealed record Summary(string Repo, string Dependency)
    {
        public static Summary? Parse(string title)
        {
            var match = SummaryTitle().Match(Normalised(title));
            return match.Success
                ? new Summary(match.Groups["repo"].Value, match.Groups["dep"].Success ? match.Groups["dep"].Value : match.Groups["repo"].Value)
                : null;
        }

        public bool Matches(RenovateUpdate update) =>
            update.SourceRepo is null
                ? string.Equals(Dependency, Normalised(update.Dependency), StringComparison.OrdinalIgnoreCase)
                : string.Equals(Repo, update.SourceRepo, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Block(Summary Summary, string? Newest, bool NewestHasNotes)
    {
        public static Block Of(Summary summary, List<Line> content)
        {
            var start = content.FindIndex(IsVersionHeading);
            if (start < 0)
            {
                return new Block(summary, null, false);
            }

            var next = content.FindIndex(start + 1, IsVersionHeading);
            var section = content.Skip(start + 1).Take(next < 0 ? content.Count : next - start - 1);
            return new Block(summary, VersionOf(content[start]), HasListItemWithText(section));
        }

        public bool Covers(RenovateUpdate update) =>
            NewestHasNotes && Summary.Matches(update) && string.Equals(Newest, Bare(update.To), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*#{1,6}\s+Release Notes\s*$", Linear | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReleaseNotesHeading();

    [GeneratedRegex(@"^\s{0,3}#{1,3}\s+Configuration\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConfigurationHeading();

    [GeneratedRegex(@"^\s*<details(?:\s[^>]*)?>", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DetailsOpen();

    [GeneratedRegex(@"^\s*</details\s*>\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DetailsClose();

    [GeneratedRegex(@"^\s*<summary>(?<title>.*)</summary>\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^(?<repo>\S+)(?: \((?<dep>.+)\))?$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SummaryTitle();

    [GeneratedRegex(@"^\s{0,3}-{3,}\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+\[(?<text>[^\]]+)\]\([^)\s]*\)", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^[vV]?\d+(?:\.\d+)*", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^\s*(?:```|~~~)", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)[ \t]+(?<text>.*)$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[[^\]]*\]\([^)]*\)|\[[^\]]*\]\[[^\]]*\]|<[^>]*>|https?://\S+", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonText();

    [GeneratedRegex(@"[\p{L}\p{N}]", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LetterOrDigit();
}
