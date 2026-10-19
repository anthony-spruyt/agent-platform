using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public static partial class ReleaseNotesDetector
{
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const int IndentedCodeWidth = 4;

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
        if (body is null)
        {
            return false;
        }

        var updates = Updates(body);
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

        var updates = Updates(body);
        if (!updates.All(ChangelogFetcher.IsFetchable))
        {
            return false;
        }

        var fetchable = ChangelogFetcher.FetchableUpdates(updates).Count;
        return fetchable <= ChangelogFetcher.MaxDependencies && sections.Count == fetchable && sections.All(SectionHasListItem);
    }

    // A table at the row cap may hold more rows than were read, so it counts as having none.
    private static IReadOnlyList<RenovateUpdate> Updates(string? body) =>
        RenovateBodyTable.Parse(body) is { Count: < RenovateBodyTable.MaxRows } updates ? updates : [];

    private static bool SectionHasListItem(string? section) =>
        section is not null && HasListItemWithText(Clean(section));

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
        int? cut = null;
        for (var i = heading + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Fenced && StartOf(lines, i, updates) is { } started)
            {
                (open, content, depth, cut) = (started, [], 1, null);
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

            if (cut is null && (TruncationMarker().IsMatch(line.Text) || ConfigurationHeading().IsMatch(line.Text)))
            {
                cut = content.Count;
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

        if (open is not null && cut is { } end)
        {
            blocks.Add(Block.Of(open, content[..end]));
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
        var afterBlank = true;
        var indentedCode = false;
        var inList = false;
        foreach (var raw in text.Split('\n'))
        {
            if (!commented)
            {
                var marker = Fence().IsMatch(raw);
                if (marker || fenced)
                {
                    fenced ^= marker;
                    afterBlank = true;
                    lines.Add(new Line(raw, Fenced: true));
                    continue;
                }
            }

            var visible = WithoutComments(raw, ref commented);
            if (visible.Trim().Length == 0)
            {
                afterBlank = true;
                lines.Add(new Line(visible, Fenced: false));
                continue;
            }

            indentedCode = !inList && Indent(visible) >= IndentedCodeWidth && (afterBlank || indentedCode);
            inList = !indentedCode && (ListItem().IsMatch(visible) || (inList && Indent(visible) > 0));
            afterBlank = false;
            lines.Add(new Line(visible, Fenced: indentedCode));
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

    private static bool HasListItemWithText(IEnumerable<Line> lines)
    {
        var parentIndent = -1;
        foreach (var line in lines.Where(l => !l.Fenced && l.Text.Trim().Length > 0))
        {
            var indent = Indent(line.Text);
            if (indent <= parentIndent)
            {
                parentIndent = -1;
            }

            if (ListItem().Match(line.Text) is not { Success: true } item || parentIndent >= 0)
            {
                continue;
            }

            var text = item.Groups["text"].Value;
            if (!HasText(text))
            {
                continue;
            }

            if (IsBookkeeping(text))
            {
                parentIndent = indent;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4 - width % 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    private static bool IsBookkeeping(string item) =>
        Bookkeeping().IsMatch(Attribution().Replace(Whitespace().Replace(NonText().Replace(Normalised(item), " "), " "), string.Empty));

    private static bool IsVersionHeading(Line line) => !line.Fenced && VersionHeading().IsMatch(line.Text);

    private static string VersionOf(Line heading) => Bare(VersionHeading().Match(heading.Text).Groups["version"].Value);

    private static string Bare(string version) => version.TrimStart('=', 'v', 'V');

    internal static bool CoversVersion(string to, string newest)
    {
        var target = Bare(to.Trim());
        if (string.Equals(target, newest, StringComparison.Ordinal) || RangeOrFloating().IsMatch(to.Trim()))
        {
            return true;
        }

        var core = Core().Match(target);
        if (!core.Success)
        {
            return false;
        }

        var rest = target[core.Length..];
        if (rest.StartsWith('-'))
        {
            return string.Equals(newest, core.Value, StringComparison.Ordinal);
        }

        return rest.Length == 0 && core.Value.Count(c => c == '.') < 2 && newest.StartsWith(core.Value + ".", StringComparison.Ordinal);
    }

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
                ? string.Equals(Dependency, update.Dependency, StringComparison.Ordinal)
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
            NewestHasNotes && Summary.Matches(update) && CoversVersion(update.To, Newest!);
    }

    [GeneratedRegex(@"^\s*#{1,6}\s+Release Notes\s*$", Linear | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReleaseNotesHeading();

    [GeneratedRegex(@"^\s{0,3}#{1,3}\s+Configuration\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConfigurationHeading();

    [GeneratedRegex(@"^[\s>\u2702\uFE0F]*PR body was truncated to here\.?\s*$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TruncationMarker();

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

    [GeneratedRegex(@"^### \[`v?(?<version>[^`]+)`\]\(", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^\s*(?:```|~~~)", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(?:[-*+•]|\d+[.)])[ \t]+(?<text>.*)$", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[[^\]]*\]\([^)]*\)|\[[^\]]*\]\[[^\]]*\]|<[^>]*>|https?://\S+", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonText();

    [GeneratedRegex(@"[\p{L}\p{N}]", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LetterOrDigit();

    [GeneratedRegex(@"^[\^~<>*]|[\s,|]", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RangeOrFloating();

    [GeneratedRegex(@"^\d+(?:\.\d+)*", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Core();

    [GeneratedRegex(@"\s+", Linear, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\W*(?:[0-9a-f]{7,40}\W+)?(?:Thanks\s+(?:@[\w-]+\s*)?!\s*-\s*)?", Linear | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribution();

    [GeneratedRegex(@"^(?:Updated dependencies|Release \S+-v?\d|sync\(.+\): v?\d|Full Changelog\b|(?:@?[\w-]+\s+)?made (?:their|his|her) first contribution\b)", Linear | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Bookkeeping();
}
