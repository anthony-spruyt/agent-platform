using System.Text;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.PullRequests;

public sealed record DiffFile(string Path, string Status, string? Patch);

public sealed record DiffCommit(string Sha, string Message);

public sealed record StatedVersion(string Package, string From, string To);

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

    private sealed record VersionPair(Kind Kind, string Old, string New, string Path, bool IsImageTag = false);

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

        foreach (var pair in pairs)
        {
            if (!IsStated(pair, stated))
            {
                return new DiffShapeResult(DiffShapeFailure.VersionNotStated, pair.Path);
            }
        }

        return DiffShapeResult.Pass;
    }

    private static DiffShapeResult? CheckPatch(DiffFile file, List<VersionPair> pairs)
    {
        foreach (var (removed, added) in ChangeRuns(file.Patch!))
        {
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
        }

        return null;
    }

    private static DiffShapeResult? ComparePair(string oldLine, string newLine, string path, List<VersionPair> pairs)
    {
        var (oldMask, oldTokens) = Tokenize(oldLine);
        var (newMask, newTokens) = Tokenize(newLine);
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

            pairs.Add(new VersionPair(before.Kind, before.Value, after.Value, path, before.Context == ImageTagContext));
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

            var kind = match.Groups["digest"].Success ? Kind.Digest : match.Groups["sha"].Success ? Kind.Sha : Kind.Multi;
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
                ? rows.Any(r => (StripV(r.From) == StripV(pair.Old) && StripV(r.To) == StripV(pair.New)) || (pair.IsImageTag && StatesBeforeSharedSuffix(pair, r)))
                : rows.Any(r => MajorOf(r.From) == StripV(pair.Old) && MajorOf(r.To) == StripV(pair.New));
        }

        if (stated.SubjectVersion is not { } version)
        {
            return false;
        }

        return (IsMultiPart(version) || pair.Kind == Kind.Single) && StripV(pair.New) == StripV(version);
    }

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
        if (header.IndexOf("datasource") < 0 || package < 0 || from < 0 || to < 0)
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

            read.Add(new StatedVersion(cells[package], cells[from], cells[to]));
        }

        rows = read;
        return true;
    }

    private static List<string> Cells(string line) => [.. line.Trim('|').Split('|').Select(c => c.Trim())];

    [GeneratedRegex(@"\r\n|\r|\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"uses:\s*""?[\w.-]+/[^@\s""]+@(?<sha>[0-9a-f]{40})""?\s*#\s*(?<ver>v?[0-9]+(?:\.[0-9]+)*(?:[-+][\w.]+)*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsesPin();

    [GeneratedRegex(@"(?<![\w./:@-])(?<name>(?:[a-z0-9][a-z0-9.-]*(?::[0-9]+)?/)?[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)*)(?<!(?:^|[^\w.-])sha256):(?<tag>\w[\w.+-]{0,127}?)(?<dig>@sha256:[0-9a-f]{64})?(?![\w.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"(?<digest>sha256:[0-9a-f]{64}|sha512-[A-Za-z0-9+/]{86,88}={0,2}(?![A-Za-z0-9+/=])|(?<![0-9A-Za-z])(?:[0-9a-f]{128}|[0-9a-f]{64})(?![0-9A-Za-z]))|(?<sha>(?<![0-9A-Za-z])(?:[0-9a-f]{40}|[0-9a-f]{7,12})(?![0-9A-Za-z]))|(?<multi>(?<![\w.])v?[0-9]+(?:\.[0-9]+)+(?:[-+][\w.]+)*(?![\w.]))", RegexOptions.CultureInvariant)]
    private static partial Regex AnyToken();

    [GeneratedRegex(@"^v?[0-9]+(?:\.[0-9]+)+(?:[-+][\w.]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MultiPartFull();

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
