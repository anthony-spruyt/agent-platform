using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

public sealed record ChangelogSection(string Dependency, string SourceRepo, string Origin, string Text);

public sealed partial class ChangelogFetcher(IGitHubApiClient github)
{
    public const int MaxDependencies = 5;
    public const int MaxSectionChars = 100_000;

    private const string ChangelogFile = "CHANGELOG.md";
    private const int MaxFileChars = 1_000_000;

    public async Task<IReadOnlyList<ChangelogSection>> FetchAsync(string repo, IReadOnlyList<RenovateUpdate> updates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var sections = new List<ChangelogSection>();
        foreach (var update in FetchableUpdates(updates).Take(MaxDependencies))
        {
            if (await FetchOneAsync(repo, update, cancellationToken).ConfigureAwait(false) is { } section)
            {
                sections.Add(section);
            }
        }

        return sections;
    }

    internal static bool IsFetchable(RenovateUpdate update) =>
        update.SourceRepo is not null && GitHubRepoName.TryParse(update.SourceRepo, out _, out _);

    internal static IReadOnlyList<RenovateUpdate> FetchableUpdates(IEnumerable<RenovateUpdate> updates) =>
        [.. updates.Where(IsFetchable).DistinctBy(u => (u.SourceRepo!.ToUpperInvariant(), u.To.TrimStart('v', 'V')))];

    private async Task<ChangelogSection?> FetchOneAsync(string repo, RenovateUpdate update, CancellationToken cancellationToken)
    {
        var source = update.SourceRepo!;
        var version = update.To.TrimStart('v', 'V');
        if (!PlainVersion().IsMatch(version))
        {
            return null;
        }

        var tags = new[] { $"v{version}", version, $"{update.Dependency}@{version}" }.Distinct(StringComparer.Ordinal).Where(IsPlainTag).ToList();

        foreach (var tag in tags)
        {
            var release = await github.GetReleaseByTagAsync(repo, source, tag, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(release?.Body))
            {
                return new ChangelogSection(update.Dependency, source, $"release {tag}", Cut(release.Body.Trim()));
            }
        }

        foreach (var tag in tags)
        {
            var file = await github.GetFileContentAsync(repo, source, ChangelogFile, tag, MaxFileChars, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                continue;
            }

            return ChangelogSectionText.Extract(file, version, MaxSectionChars) is { } text
                ? new ChangelogSection(update.Dependency, source, $"changelog {ChangelogFile} at {tag}", text)
                : null;
        }

        return null;
    }

    private static string Cut(string text) => text.Length <= MaxSectionChars ? text : text[..MaxSectionChars];

    private static bool IsPlainTag(string tag) => tag.Length <= 200 && PlainTag().IsMatch(tag);

    [GeneratedRegex(@"^[A-Za-z0-9_.+~-]{1,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainVersion();

    [GeneratedRegex(@"^(?!.*(?:^|/)\.\.?(?:/|$))[A-Za-z0-9_.@+~-]+(?:/[A-Za-z0-9_.@+~-]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainTag();
}
