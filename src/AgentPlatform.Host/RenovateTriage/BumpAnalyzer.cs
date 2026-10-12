using System.Globalization;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record VersionChange(string Old, string New, string Kind);

public sealed record BumpFacts(bool DigestOnly, IReadOnlyList<VersionChange> Versions, bool UnpairedVersions = false)
{
    public static BumpFacts Empty { get; } = new(false, []);
}

public static partial class BumpAnalyzer
{
    public const string Major = "major";
    public const string Minor = "minor";
    public const string Patch = "patch";

    private const string DigestPlaceholder = "<digest>";

    public static BumpFacts Analyze(IReadOnlyList<GitHubPullRequestFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var digestOnly = files.Count > 0;
        var changedBlocks = 0;
        var versions = new List<VersionChange>();
        var unpaired = false;

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.Patch))
            {
                digestOnly = false;
                continue;
            }

            foreach (var (removed, added) in ChangeBlocks(file.Patch))
            {
                changedBlocks++;
                digestOnly &= IsDigestOnly(removed, added);
                unpaired |= CollectVersions(removed, added, versions);
            }
        }

        return new BumpFacts(digestOnly && changedBlocks > 0, [.. versions.Distinct()], unpaired);
    }

    private static bool IsDigestOnly(List<string> removed, List<string> added) =>
        removed.Count == added.Count
        && removed.Count > 0
        && removed.Zip(added).All(pair => string.Equals(MaskDigests(pair.First), MaskDigests(pair.Second), StringComparison.Ordinal) || IsAppendedDigest(pair.First, pair.Second));

    private static bool IsAppendedDigest(string oldLine, string newLine) =>
        newLine.StartsWith(oldLine, StringComparison.Ordinal) && AppendedDigest().IsMatch(newLine.AsSpan(oldLine.Length));

    private static string MaskDigests(string line) => Sha256Digest().Replace(GitSha().Replace(line, DigestPlaceholder), DigestPlaceholder);

    private static bool CollectVersions(List<string> removed, List<string> added, List<VersionChange> versions)
    {
        var unpaired = false;
        foreach (var (oldLine, newLine) in removed.Zip(added))
        {
            var oldTokens = VersionTokens(oldLine);
            var newTokens = VersionTokens(newLine);
            if (oldTokens.Count != newTokens.Count)
            {
                unpaired = true;
                continue;
            }

            for (var i = 0; i < oldTokens.Count; i++)
            {
                var kind = KindOfChange(oldTokens[i], newTokens[i]);
                if (kind is not null)
                {
                    versions.Add(new VersionChange(oldTokens[i].Text, newTokens[i].Text, kind));
                }
            }
        }

        return unpaired;
    }

    private static string? KindOfChange(VersionToken before, VersionToken after)
    {
        if (before.Major != after.Major)
        {
            return Major;
        }

        if (before.Minor != after.Minor)
        {
            return Minor;
        }

        return before.Patch != after.Patch ? Patch : null;
    }

    private static List<VersionToken> VersionTokens(string line)
    {
        var tokens = new List<VersionToken>();
        foreach (Match match in SemverLike().Matches(line))
        {
            if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                && TryParsePatch(match.Groups[3], out var patch))
            {
                tokens.Add(new VersionToken(match.Value, major, minor, patch));
            }
        }

        return tokens;
    }

    private static bool TryParsePatch(Group group, out long patch)
    {
        patch = 0;
        return !group.Success || long.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch);
    }

    private static IEnumerable<(List<string> Removed, List<string> Added)> ChangeBlocks(string patch)
    {
        var removed = new List<string>();
        var added = new List<string>();
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith('\\'))
            {
                continue;
            }

            if (line.StartsWith('-'))
            {
                if (added.Count > 0)
                {
                    yield return (removed, added);
                    removed = [];
                    added = [];
                }

                removed.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
            else if (removed.Count > 0 || added.Count > 0)
            {
                yield return (removed, added);
                removed = [];
                added = [];
            }
        }

        if (removed.Count > 0 || added.Count > 0)
        {
            yield return (removed, added);
        }
    }

    private sealed record VersionToken(string Text, long Major, long Minor, long Patch);

    [GeneratedRegex("sha256:[0-9a-fA-F]{64}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex(@"^@sha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppendedDigest();

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-fA-F]{40}(?![0-9a-fA-F])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GitSha();

    [GeneratedRegex(@"(?<![0-9A-Za-z.])v?(\d+)\.(\d+)(?:\.(\d+))?(?:-[0-9A-Za-z.-]+)?(?![0-9A-Za-z]|\.\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SemverLike();
}
