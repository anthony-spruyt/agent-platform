using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RenovateTriage;

public static partial class ReleaseNotesDetector
{
    public static bool Found(string? body, IReadOnlyList<string>? sections) =>
        BodyHasNotes(body) || (sections is { Count: > 0 } && sections.All(SectionHasNotes));

    public static bool BodyHasNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var lines = Lines(body);
        var heading = Array.FindIndex(lines, l => ReleaseNotesHeading().IsMatch(l));
        if (heading < 0)
        {
            return false;
        }

        var blocks = new List<List<string>>();
        List<string>? block = null;
        var depth = 0;
        foreach (var line in lines.Skip(heading + 1))
        {
            if (block is null)
            {
                if (DetailsOpen().IsMatch(line))
                {
                    block = [];
                }
                else if (TopHeading().IsMatch(line))
                {
                    break;
                }
                else
                {
                    continue;
                }
            }

            block.Add(line);
            depth += DetailsOpen().Count(line) - DetailsClose().Count(line);
            if (depth <= 0)
            {
                blocks.Add(block);
                block = null;
                depth = 0;
            }
        }

        return block is null && blocks.Count > 0 && blocks.All(BlockHasNotes);
    }

    public static bool SectionHasNotes(string? section) =>
        !string.IsNullOrWhiteSpace(section) && HasListItemWithText(Lines(section));

    private static bool BlockHasNotes(List<string> block)
    {
        var start = block.FindIndex(IsVersionHeading);
        if (start < 0)
        {
            return false;
        }

        var next = block.FindIndex(start + 1, IsVersionHeading);
        return HasListItemWithText(block.Skip(start).Take(next < 0 ? block.Count : next - start));
    }

    private static bool HasListItemWithText(IEnumerable<string> lines)
    {
        var fenced = false;
        foreach (var line in lines)
        {
            if (Fence().IsMatch(line))
            {
                fenced = !fenced;
            }
            else if (!fenced && ListItem().Match(line) is { Success: true } item && HasText(item.Groups["text"].Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVersionHeading(string line) =>
        VersionHeading().Match(line) is { Success: true } heading
        && VersionText().IsMatch(heading.Groups["text"].Value.Replace("`", string.Empty, StringComparison.Ordinal).Trim());

    private static bool HasText(string item) => LetterOrDigit().IsMatch(NonText().Replace(item, " "));

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    [GeneratedRegex(@"^\s*#{1,6}\s+Release Notes\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReleaseNotesHeading();

    [GeneratedRegex(@"^\s{0,3}#{1,3}\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TopHeading();

    [GeneratedRegex(@"<details(?:\s[^>]*)?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DetailsOpen();

    [GeneratedRegex(@"</details\s*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DetailsClose();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+\[(?<text>[^\]]+)\]\([^)\s]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^[vV]?\d+(?:\.\d+)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^\s*(?:```|~~~)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)(?:[ \t]+(?<text>.*))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"!?\[[^\]]*\]\([^)]*\)|\[[^\]]*\]\[[^\]]*\]|<!--.*?-->|<[^>]*>|https?://\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonText();

    [GeneratedRegex(@"[\p{L}\p{N}]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LetterOrDigit();
}
