using Microsoft.Extensions.FileSystemGlobbing;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record MigrationMatch(string? Path, string? Dependency);

public sealed class MigrationDetector
{
    public const string Reason = "hands-on-migration";

    private readonly Matcher _paths;
    private readonly Matcher _dependencies;

    public MigrationDetector(RenovateTriageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _paths = Build(options.Migration?.PathGlobs ?? MigrationOptions.DefaultPathGlobs, StringComparison.Ordinal);
        _dependencies = Build(options.Migration?.DependencyGlobs ?? MigrationOptions.DefaultDependencyGlobs, StringComparison.OrdinalIgnoreCase);
    }

    public static TriageDecision Decision(MigrationMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var what = match.Path is not null ? $"changes `{match.Path}`" : $"updates `{match.Dependency}`";
        return new TriageDecision(TriageVerdict.NeedsReview, Reason, $"**Needs review:** this PR {what}, which needs a hands-on migration.");
    }

    public MigrationMatch? Detect(IReadOnlyCollection<string> changedPaths, IReadOnlyCollection<string> dependencies)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (First(_paths, changedPaths) is { } path)
        {
            return new MigrationMatch(path, null);
        }

        return First(_dependencies, dependencies) is { } dependency ? new MigrationMatch(null, dependency) : null;
    }

    private static string? First(Matcher matcher, IReadOnlyCollection<string> candidates) =>
        candidates.FirstOrDefault(c => matcher.Match(c).HasMatches);

    private static Matcher Build(IEnumerable<string> globs, StringComparison comparison)
    {
        var matcher = new Matcher(comparison);
        matcher.AddIncludePatterns(globs);
        return matcher;
    }
}
