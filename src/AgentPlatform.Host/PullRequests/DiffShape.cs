using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.PullRequests;

public sealed record DiffFile(string Path, string Status, string? Patch);

public sealed record DiffCommit(string Sha, string Message);

public sealed record StatedVersion(string Package, string From, string To, string Datasource = "");

public sealed record StatedVersions(IReadOnlyList<StatedVersion>? Rows, string? SubjectVersion);

public enum DiffShapeFailure
{
    None,
    HeadMoved,
    HeadCommitMismatch,
    FileCountMismatch,
    MalformedTable,
    FileNotModified,
    MissingPatch,
    UnpairedLines,
    NotAVersionChange,
    HexLengthChanged,
    CommentWithoutShaChange,
    VersionNotStated,
}

public sealed record DiffShapeResult(DiffShapeFailure Failure, string? Detail = null)
{
    public static DiffShapeResult Pass { get; } = new(DiffShapeFailure.None);

    public bool Passed => Failure == DiffShapeFailure.None;
}

public static partial class DiffShape
{
    private const char Placeholder = '\0';
    private const string UsesShaContext = "uses-sha";
    private const string UsesCommentContext = "uses-comment";
    private const string ImageTagContext = "image-tag";

    private static readonly HashSet<string> _lockfiles = new(StringComparer.Ordinal)
    {
        "package-lock.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "uv.lock",
        "poetry.lock",
        "Cargo.lock",
        "Chart.lock",
        "go.sum",
        ".terraform.lock.hcl",
        "packages.lock.json",
    };

    private enum Kind
    {
        Digest,
        Sha,
        Multi,
        Single,
    }

    private sealed record Token(int Start, int End, Kind Kind, string Value, string? Context = null);

    private sealed record VersionPair(Kind Kind, string Old, string New, string Path, object Line, string Head, string? Context = null, bool IsIndirect = false, bool FollowsLessThan = false);

    public static DiffShapeResult Classify(
        IReadOnlyList<DiffFile> files,
        int expectedFileCount,
        string headShaRead,
        IReadOnlyList<DiffCommit> commits,
        string headShaReread,
        string statedMessage)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(commits);

        if (!string.Equals(headShaRead, headShaReread, StringComparison.OrdinalIgnoreCase))
        {
            return new DiffShapeResult(DiffShapeFailure.HeadMoved);
        }

        if (commits.Count == 0 || !string.Equals(commits[^1].Sha, headShaRead, StringComparison.OrdinalIgnoreCase))
        {
            return new DiffShapeResult(DiffShapeFailure.HeadCommitMismatch);
        }

        if (files.Count != expectedFileCount)
        {
            return new DiffShapeResult(DiffShapeFailure.FileCountMismatch);
        }

        if (ReadStatedVersions(statedMessage) is not { } stated)
        {
            return new DiffShapeResult(DiffShapeFailure.MalformedTable);
        }

        var pairs = new List<VersionPair>();
        foreach (var file in files)
        {
            if (string.Equals(file.Status, "modified", StringComparison.Ordinal) && _lockfiles.Contains(FileName(file.Path)))
            {
                continue;
            }

            if (!string.Equals(file.Status, "modified", StringComparison.Ordinal))
            {
                return new DiffShapeResult(DiffShapeFailure.FileNotModified, file.Path);
            }

            if (string.IsNullOrEmpty(file.Patch))
            {
                return new DiffShapeResult(DiffShapeFailure.MissingPatch, file.Path);
            }

            var failure = CheckPatch(file, pairs);
            if (failure is not null)
            {
                return failure;
            }
        }

        var statedPairs = new List<VersionPair>();
        var bounded = new HashSet<VersionPair>(ReferenceEqualityComparer.Instance);
        foreach (var pair in pairs)
        {
            if (IsStated(pair, stated))
            {
                statedPairs.Add(pair);
            }
            else if (!TakeRangeUpperBound(pair, statedPairs, bounded) && !IsIndirectUpgrade(pair, pairs, stated))
            {
                return new DiffShapeResult(DiffShapeFailure.VersionNotStated, pair.Path);
            }
        }

        return DiffShapeResult.Pass;
    }

    private static DiffShapeResult? CheckPatch(DiffFile file, List<VersionPair> pairs)
    {
        foreach (var (allRemoved, allAdded) in ChangeRuns(file.Patch!))
        {
            var pairsBefore = pairs.Count;
            var removed = allRemoved.Where(l => !PipHashLine().IsMatch(l)).ToList();
            var added = allAdded.Where(l => !PipHashLine().IsMatch(l)).ToList();
            if (removed.Count != added.Count)
            {
                return new DiffShapeResult(DiffShapeFailure.UnpairedLines, file.Path);
            }

            for (var i = 0; i < removed.Count; i++)
            {
                var failure = ComparePair(removed[i], added[i], file.Path, pairs);
                if (failure is not null)
                {
                    return failure;
                }
            }

            var droppedHashes = removed.Count + added.Count < allRemoved.Count + allAdded.Count;
            if (droppedHashes && pairs.Count == pairsBefore)
            {
                return new DiffShapeResult(DiffShapeFailure.NotAVersionChange, file.Path);
            }
        }

        return null;
    }

    private static DiffShapeResult? ComparePair(string oldLine, string newLine, string path, List<VersionPair> pairs)
    {
        var (oldMask, oldTokens) = Tokenize(oldLine);
        var (newMask, newTokens) = Tokenize(newLine);
        var line = new object();
        var indirectAt = FileName(path) == "go.mod" ? IndirectVersionStart(oldLine, newLine) : null;
        if (!string.Equals(oldMask, newMask, StringComparison.Ordinal)
            || oldTokens.Count != newTokens.Count
            || oldTokens.Zip(newTokens).Any(t => t.First.Kind != t.Second.Kind))
        {
            return new DiffShapeResult(DiffShapeFailure.NotAVersionChange, path);
        }

        var shaChanged = oldTokens.Zip(newTokens).Any(t =>
            t.First.Kind == Kind.Sha
            && t.First.Context == UsesShaContext
            && !string.Equals(t.First.Value, t.Second.Value, StringComparison.Ordinal));

        foreach (var (before, after) in oldTokens.Zip(newTokens))
        {
            if (string.Equals(before.Value, after.Value, StringComparison.Ordinal) || before.Kind == Kind.Digest)
            {
                continue;
            }

            if (before.Kind == Kind.Sha)
            {
                if (before.Value.Length != after.Value.Length)
                {
                    return new DiffShapeResult(DiffShapeFailure.HexLengthChanged, path);
                }

                continue;
            }

            if (before.Context == UsesCommentContext && !shaChanged)
            {
                return new DiffShapeResult(DiffShapeFailure.CommentWithoutShaChange, path);
            }

            var head = oldLine[..before.Start];
            var followsLessThan = head.EndsWith('<') || head.EndsWith("<=", StringComparison.Ordinal);
            pairs.Add(new VersionPair(before.Kind, before.Value, after.Value, path, line, head, before.Context, before.Start == indirectAt, followsLessThan));
        }

        return null;
    }

    private static List<(List<string> Removed, List<string> Added)> ChangeRuns(string patch)
    {
        var runs = new List<(List<string>, List<string>)>();
        List<string> removed = [];
        List<string> added = [];

        void Flush()
        {
            if (removed.Count > 0 || added.Count > 0)
            {
                runs.Add((removed, added));
            }

            removed = [];
            added = [];
        }

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
                    Flush();
                }

                removed.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return runs;
    }

    private static (string Mask, List<Token> Tokens) Tokenize(string line)
    {
        var tokens = new List<Token>();

        var uses = UsesPin().Match(line);
        if (uses.Success)
        {
            var version = uses.Groups["ver"];
            tokens.Add(new Token(version.Index, version.Index + version.Length, IsMultiPart(version.Value) ? Kind.Multi : Kind.Single, version.Value, UsesCommentContext));
            var sha = uses.Groups["sha"];
            tokens.Add(new Token(sha.Index, sha.Index + sha.Length, Kind.Sha, sha.Value, UsesShaContext));
        }

        var occupied = tokens.Select(t => (t.Start, t.End)).ToList();
        foreach (Match image in ImageReference().Matches(line))
        {
            if (Overlaps(occupied, image.Index, image.Index + image.Length) || !NameHasLetter(image.Groups["name"].Value))
            {
                continue;
            }

            var tag = image.Groups["tag"];
            var tagKind = ClassifyImageTag(tag.Value);
            if (tagKind is { } kind)
            {
                tokens.Add(new Token(tag.Index, tag.Index + tag.Length, kind, tag.Value, ImageTagContext));
            }

            occupied.Add((image.Index, image.Index + image.Length));
            var digest = image.Groups["dig"];
            if (digest.Success)
            {
                tokens.Add(new Token(digest.Index + 1, digest.Index + digest.Length, Kind.Digest, digest.Value[1..]));
            }
        }

        foreach (Match match in AnyToken().Matches(line))
        {
            if (Overlaps(occupied, match.Index, match.Index + match.Length))
            {
                continue;
            }

            var kind = MatchKind(match);
            if (kind == Kind.Sha && match.Length != 40 && !HexLetter().IsMatch(match.Value))
            {
                continue;
            }

            tokens.Add(new Token(match.Index, match.Index + match.Length, kind, match.Value));
            occupied.Add((match.Index, match.Index + match.Length));
        }

        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        var mask = new StringBuilder();
        var position = 0;
        foreach (var token in tokens)
        {
            mask.Append(line, position, token.Start - position).Append(Placeholder);
            position = token.End;
        }

        mask.Append(line, position, line.Length - position);
        return (mask.ToString(), tokens);
    }

    private static Kind MatchKind(Match match)
    {
        if (match.Groups["digest"].Success)
        {
            return Kind.Digest;
        }

        if (match.Groups["sha"].Success)
        {
            return Kind.Sha;
        }

        return match.Groups["single"].Success ? Kind.Single : Kind.Multi;
    }

    private static bool Overlaps(List<(int Start, int End)> occupied, int start, int end) =>
        occupied.Exists(o => start < o.End && end > o.Start);

    private static bool NameHasLetter(string name) => AsciiLetter().IsMatch(name[(name.LastIndexOf('/') + 1)..]);

    private static Kind? ClassifyImageTag(string tag)
    {
        if (MultiPartFull().IsMatch(tag))
        {
            return Kind.Multi;
        }

        if (SinglePartFull().IsMatch(tag))
        {
            return Kind.Single;
        }

        return ShaFull().IsMatch(tag) && HexLetter().IsMatch(tag) ? Kind.Sha : null;
    }

    private static bool IsMultiPart(string version) => version.Split('-')[0].Split('+')[0].Contains('.', StringComparison.Ordinal);

    public static StatedVersions? ReadStatedVersions(string commitMessage)
    {
        ArgumentNullException.ThrowIfNull(commitMessage);
        var lines = LineBreak().Split(commitMessage);
        if (!TryReadTable(lines, out var rows))
        {
            return null;
        }

        var match = SubjectVersion().Match(lines[0]);
        return new StatedVersions(rows, match.Success ? match.Groups["ver"].Value : null);
    }

    private static bool IsStated(VersionPair pair, StatedVersions stated)
    {
        if (stated.Rows is { } rows)
        {
            return pair.Kind == Kind.Multi
                ? rows.Any(r => StatesMultiPart(pair, r))
                : rows.Any(r => MajorOf(r.From) == StripV(pair.Old) && MajorOf(r.To) == StripV(pair.New));
        }

        if (stated.SubjectVersion is not { } version)
        {
            return false;
        }

        return (IsMultiPart(version) || pair.Kind == Kind.Single) && StripV(pair.New) == StripV(version);
    }

    private static bool StatesMultiPart(VersionPair pair, StatedVersion row) =>
        (IsDotPrefix(StripV(pair.Old), StripV(row.From)) && IsDotPrefix(StripV(pair.New), StripV(row.To)))
        || (AllowsSharedSuffix(pair, row) && StatesBeforeSharedSuffix(pair, row))
        || StatesAfterSharedPrefix(pair, row)
        || StatesRangeLowerBound(pair, row);

    private static bool IsDotPrefix(string prefix, string version) =>
        version.StartsWith(prefix, StringComparison.Ordinal) && (version.Length == prefix.Length || version[prefix.Length] == '.');

    private static bool TakeRangeUpperBound(VersionPair pair, List<VersionPair> statedPairs, HashSet<VersionPair> bounded)
    {
        var (old, next) = (StripV(pair.Old).Split('.'), StripV(pair.New).Split('.'));
        if (!pair.FollowsLessThan
            || pair.Kind != Kind.Multi
            || old.Length != next.Length
            || !old.AsSpan(..^1).SequenceEqual(next.AsSpan(..^1))
            || !int.TryParse(old[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || next[^1] != (last + 1).ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        var lower = statedPairs.Find(a => a.Line == pair.Line && a.New == pair.Old && !bounded.Contains(a));
        return lower is not null && bounded.Add(lower);
    }

    private static int? IndirectVersionStart(string oldLine, string newLine)
    {
        var (before, after) = (GoIndirectRequire().Match(oldLine), GoIndirectRequire().Match(newLine));
        return before.Success && after.Success
            && string.Equals(before.Groups["mod"].Value, after.Groups["mod"].Value, StringComparison.Ordinal)
            && before.Groups["ver"].Index == after.Groups["ver"].Index
            ? before.Groups["ver"].Index
            : null;
    }

    private static bool IsIndirectUpgrade(VersionPair pair, List<VersionPair> pairs, StatedVersions stated)
    {
        if (!pair.IsIndirect || pair.Kind != Kind.Multi || !IsGoUpgrade(pair.Old, pair.New) || stated.Rows is not { } rows)
        {
            return false;
        }

        StatedVersions goRows = new([.. rows.Where(r => r.Datasource == "go")], null);
        return pairs.Exists(p => p.Path == pair.Path && IsStated(p, goRows));
    }

    private static bool IsGoUpgrade(string old, string next) =>
        Version.TryParse(GoCore(old), out var from) && Version.TryParse(GoCore(next), out var to) && to > from;

    private static string GoCore(string version) => StripV(version).Split('-', '+')[0];

    private static bool StatesAfterSharedPrefix(VersionPair pair, StatedVersion row) =>
        SplitDashPrefix(row.From) is { } from
        && SplitDashPrefix(row.To) is { } to
        && string.Equals(from.Prefix, to.Prefix, StringComparison.Ordinal)
        && pair.Head.EndsWith(from.Prefix, StringComparison.Ordinal)
        && IsDotPrefix(StripV(pair.Old), StripV(from.Version))
        && IsDotPrefix(StripV(pair.New), StripV(to.Version));

    private static (string Prefix, string Version)? SplitDashPrefix(string value)
    {
        for (var dash = value.IndexOf('-', StringComparison.Ordinal); dash >= 0; dash = value.IndexOf('-', dash + 1))
        {
            if (MultiPartFull().IsMatch(value[(dash + 1)..]))
            {
                return (value[..(dash + 1)], value[(dash + 1)..]);
            }
        }

        return null;
    }

    private static bool StatesRangeLowerBound(VersionPair pair, StatedVersion row) =>
        RangeOperatorEnd().IsMatch(pair.Head) && IsSameMajorAndNotAbove(pair.Old, row.From) && IsSameMajorAndNotAbove(pair.New, row.To);

    private static bool IsSameMajorAndNotAbove(string bound, string stated)
    {
        if (DottedNumbers(bound) is not { } low || DottedNumbers(stated) is not { } high || low[0] != high[0])
        {
            return false;
        }

        for (var i = 0; i < Math.Max(low.Length, high.Length); i++)
        {
            var (lowPart, highPart) = (i < low.Length ? low[i] : 0, i < high.Length ? high[i] : 0);
            if (lowPart != highPart)
            {
                return lowPart < highPart;
            }
        }

        return true;
    }

    private static int[]? DottedNumbers(string version)
    {
        var parts = StripV(version).Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    // A suffix after the version is docker tag syntax: other datasources have to state it in their row.
    private static bool AllowsSharedSuffix(VersionPair pair, StatedVersion row) =>
        pair.Context == ImageTagContext || (pair.Context is null && string.Equals(row.Datasource, "docker", StringComparison.Ordinal));

    private static bool StatesBeforeSharedSuffix(VersionPair pair, StatedVersion row)
    {
        if (!IsMultiPart(row.From) || !IsMultiPart(row.To))
        {
            return false;
        }

        var (from, to, oldTag, newTag) = (StripV(row.From), StripV(row.To), StripV(pair.Old), StripV(pair.New));
        if (!oldTag.StartsWith(from, StringComparison.Ordinal) || !newTag.StartsWith(to, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = oldTag[from.Length..];
        return suffix.Length > 1 && suffix[0] == '-' && string.Equals(newTag[to.Length..], suffix, StringComparison.Ordinal);
    }

    private static string StripV(string version) => version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;

    private static string? MajorOf(string version)
    {
        var match = LeadingDigits().Match(StripV(version));
        return match.Success ? match.Value : null;
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool TryReadTable(string[] lines, out IReadOnlyList<StatedVersion>? rows)
    {
        rows = null;
        var tableLines = lines.Skip(1).Select(l => l.Trim()).Where(l => l.StartsWith('|')).ToList();
        if (tableLines.Count == 0)
        {
            return true;
        }

        var header = Cells(tableLines[0]).Select(c => c.ToLowerInvariant()).ToList();
        var from = header.IndexOf("from");
        var to = header.IndexOf("to");
        var package = header.IndexOf("package");
        var datasource = header.IndexOf("datasource");
        if (datasource < 0 || package < 0 || from < 0 || to < 0)
        {
            return false;
        }

        var read = new List<StatedVersion>();
        foreach (var line in tableLines.Skip(1))
        {
            var cells = Cells(line);
            if (cells.TrueForAll(c => SeparatorCell().IsMatch(c)))
            {
                continue;
            }

            if (cells.Count != header.Count)
            {
                return false;
            }

            read.Add(new StatedVersion(cells[package], cells[from], cells[to], cells[datasource]));
        }

        rows = read;
        return true;
    }

    private static List<string> Cells(string line) => [.. line.Trim('|').Split('|').Select(c => c.Trim())];

    [GeneratedRegex(@"\r\n|\r|\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"^\s*--hash=sha256:[0-9a-f]{64}(?: \\)?\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex PipHashLine();

    [GeneratedRegex(@"^\s*(?:require\s+)?(?<mod>\S*[./]\S*)\s+(?<ver>v\S+)\s+// indirect\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex GoIndirectRequire();

    [GeneratedRegex(@"uses:\s*""?[\w.-]+/[^@\s""]+@(?<sha>[0-9a-f]{40})""?\s*#\s*(?<ver>v?[0-9]+(?:\.[0-9]+)*(?:[-+][\w.]+)*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsesPin();

    [GeneratedRegex(@"(?<![\w./:@-])(?<name>(?:[a-z0-9][a-z0-9.-]*(?::[0-9]+)?/)?[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)*)(?<!(?:^|[^\w.-])sha256):(?<tag>\w[\w.+-]{0,127}?)(?<dig>@sha256:[0-9a-f]{64})?(?![\w.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"(?<digest>sha256:[0-9a-f]{64}|sha512-[A-Za-z0-9+/]{86,88}={0,2}(?![A-Za-z0-9+/=])|(?<![0-9A-Za-z])(?:[0-9a-f]{128}|[0-9a-f]{64})(?![0-9A-Za-z]))|(?<sha>(?<![0-9A-Za-z])(?:[0-9a-f]{40}|[0-9a-f]{7,12})(?![0-9A-Za-z]))|(?<multi>(?<![\w.])v?[0-9]+(?:\.[0-9]+)+(?:(?:\.post|\.dev|a|b|rc)[0-9]+)?(?:[-+][\w.]+)*(?![\w.]))|(?<single>(?<="")v?[0-9]+(?=""))", RegexOptions.CultureInvariant)]
    private static partial Regex AnyToken();

    [GeneratedRegex(@"^v?[0-9]+(?:\.[0-9]+)+(?:[-+][\w.]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MultiPartFull();

    [GeneratedRegex(@"(?:\^|~|~> ?|>=)$", RegexOptions.CultureInvariant)]
    private static partial Regex RangeOperatorEnd();

    [GeneratedRegex(@"^v?[0-9]+(?:[-+][\w.]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SinglePartFull();

    [GeneratedRegex(@"^(?:[0-9a-f]{40}|[0-9a-f]{7,12})$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaFull();

    [GeneratedRegex("[a-f]", RegexOptions.CultureInvariant)]
    private static partial Regex HexLetter();

    [GeneratedRegex("[a-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiLetter();

    [GeneratedRegex(@"\bto (?<ver>v?[0-9]+(?:\.[0-9]+)*(?:[-+][\w.]+)*)(?: \[security\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectVersion();

    [GeneratedRegex("^[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDigits();

    [GeneratedRegex("^:?-+:?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorCell();
}
