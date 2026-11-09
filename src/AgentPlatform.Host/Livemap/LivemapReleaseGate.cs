using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

// Everything the gate reads comes from the GitHub API for the pull request's current head sha; CiSuiteIds holds the check suites of the fork's own CI run for it.
public sealed record LivemapReleaseFacts(
    LivemapPullRequest Pull,
    string Fork,
    string ReleaseBot,
    string MainHead,
    IReadOnlyList<string> HeadParents,
    IReadOnlyList<LivemapPullRequestFile> Files,
    string? VersionAtHead,
    IReadOnlyList<LivemapCheckRun> CheckRuns,
    IReadOnlySet<long> CiSuiteIds);

public static partial class LivemapReleaseGate
{
    public const string Branch = "release-please--branches--main";

    private const string MainBranch = "main";

    private const string VersionPath = "version.txt";

    private const string CMakePath = "CMakeLists.txt";

    private static readonly string[] _releaseFiles = [".release-please-manifest.json", "CHANGELOG.md", VersionPath, CMakePath];

    public static string? VersionOf(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return ReleaseTitle().Match(title) is { Success: true } match ? match.Groups["version"].Value : null;
    }

    public static LivemapGateVerdict Evaluate(LivemapReleaseFacts facts, IReadOnlyList<string> requiredChecks)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(requiredChecks);

        if (requiredChecks.Count == 0)
        {
            return LivemapGateVerdict.Refused("No required checks are set.");
        }

        if (Refusal(facts) is { } reason)
        {
            return LivemapGateVerdict.Refused(reason);
        }

        return facts.HeadParents[0] != facts.MainHead ? LivemapGateVerdict.MainMoved : LivemapMergeGate.Checks(facts.CheckRuns, facts.CiSuiteIds, requiredChecks);
    }

    private static string? Refusal(LivemapReleaseFacts facts)
    {
        var pull = facts.Pull;
        if (pull.State != "open" || pull.Merged)
        {
            return "The pull request is not open.";
        }

        if (pull.Draft)
        {
            return "The pull request is a draft.";
        }

        if (!SameRepository(pull.HeadRepo, facts.Fork) || !SameRepository(pull.BaseRepo, facts.Fork))
        {
            return "The pull request is not between branches of the fork.";
        }

        if (pull.BaseRef != MainBranch || pull.HeadRef != Branch)
        {
            return "The pull request is not the release branch against main.";
        }

        if (!string.Equals(pull.AuthorLogin, facts.ReleaseBot, StringComparison.OrdinalIgnoreCase) || pull.AuthorType != "Bot")
        {
            return "The pull request was not opened by the release bot.";
        }

        if (VersionOf(pull.Title) is not { } version)
        {
            return "The pull request title is not a plain release title.";
        }

        if (pull.Commits != 1 || facts.HeadParents.Count != 1)
        {
            return "The pull request is not a single commit.";
        }

        return FilesRefusal(facts.Files, version) ?? (facts.VersionAtHead?.Trim() == version ? null : $"{VersionPath} is not the release version at the head.");
    }

    private static string? FilesRefusal(IReadOnlyList<LivemapPullRequestFile> files, string version)
    {
        if (files.Any(file => !_releaseFiles.Contains(file.Filename) || file.Status != "modified" || file.PreviousFilename is not null)
            || !files.Any(file => file.Filename == VersionPath))
        {
            return "The pull request does not change only the release files.";
        }

        return files.FirstOrDefault(file => file.Filename == CMakePath) is { } cmake && !MovesOnlyTheProjectVersion(cmake.Patch, version)
            ? $"{CMakePath} changes more than the project version."
            : null;
    }

    private static bool MovesOnlyTheProjectVersion(string? patch, string version)
    {
        if (patch is null)
        {
            return false;
        }

        var lines = patch.Split('\n');
        var removed = lines.Where(line => line.StartsWith('-')).ToList();
        var added = lines.Where(line => line.StartsWith('+')).ToList();
        if (removed.Count != 1 || added.Count != 1)
        {
            return false;
        }

        var before = ProjectLine().Match(removed[0][1..]);
        var after = ProjectLine().Match(added[0][1..]);
        return before.Success
            && after.Success
            && before.Groups["name"].Value == after.Groups["name"].Value
            && before.Groups["rest"].Value == after.Groups["rest"].Value
            && after.Groups["version"].Value == version;
    }

    private static bool SameRepository(string repository, string fork) => string.Equals(repository, fork, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^chore\(main\): release (?<version>[0-9]+\.[0-9]+\.[0-9]+)\z")]
    private static partial Regex ReleaseTitle();

    [GeneratedRegex(@"^project\((?<name>[A-Za-z0-9_.-]+) VERSION (?<version>[0-9]+\.[0-9]+\.[0-9]+)(?<rest>[ )].*)\z")]
    private static partial Regex ProjectLine();
}
