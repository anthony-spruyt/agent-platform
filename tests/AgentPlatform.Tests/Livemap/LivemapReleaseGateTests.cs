using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapReleaseGateTests
{
    private const string Fork = "octo/livemap";

    private const string Bot = "release-bot[bot]";

    private const string Branch = "release-please--branches--main";

    private const long CiSuite = 7000;

    private const string Patch = "@@ -3,7 +3,7 @@ cmake_minimum_required(VERSION 3.20)\n \n-project(live_map VERSION 1.9.0 LANGUAGES CXX ASM_MASM) # x-release-please-version\n+project(live_map VERSION 1.10.0 LANGUAGES CXX ASM_MASM) # x-release-please-version\n \n set(CMAKE_CXX_STANDARD 20)";

    private static readonly string Head = new('a', 40);

    private static readonly string Main = new('b', 40);

    private static readonly string[] _required = ["lint / MegaLinter", "repo / Build, test, package"];

    private static LivemapReleaseFacts Good() => new(
        Pull: new LivemapPullRequest(33, "open", false, false, "main", Fork, Branch, Fork, Head, Bot, "Bot", 0, "chore(main): release 1.10.0"),
        Fork: Fork,
        ReleaseBot: Bot,
        HeadParents: [Main],
        Files:
        [
            new LivemapPullRequestFile(".release-please-manifest.json", "modified", null, "@@ -1,3 +1,3 @@"),
            new LivemapPullRequestFile("CHANGELOG.md", "modified", null, "@@ -1,3 +1,9 @@"),
            new LivemapPullRequestFile("CMakeLists.txt", "modified", null, Patch),
            new LivemapPullRequestFile("version.txt", "modified", null, "@@ -1 +1 @@\n-1.9.0\n+1.10.0"),
        ],
        VersionAtHead: "1.10.0\n",
        CheckRuns: [.. _required.Select(name => Run(name))],
        CiSuiteIds: new HashSet<long> { CiSuite });

    private static LivemapCheckRun Run(string name, string status = "completed", string? conclusion = "success", string? app = "github-actions", long suite = CiSuite) =>
        new(name, status, status == "completed" ? conclusion : null, app, suite);

    private static LivemapGateVerdict Evaluate(LivemapReleaseFacts facts, params string[] required) =>
        LivemapReleaseGate.Evaluate(facts, required.Length == 0 ? _required : required);

    private static LivemapReleaseFacts WithCMake(LivemapReleaseFacts facts, string? patch) =>
        facts with { Files = [.. facts.Files.Select(file => file.Filename == "CMakeLists.txt" ? file with { Patch = patch } : file)] };

    [Fact]
    public void AReleasePullRequestWithEveryRequiredCheckGreen_Passes()
    {
        Assert.Equal(LivemapGateOutcome.Passed, Evaluate(Good()).Outcome);
    }

    [Fact]
    public void TheReleaseNeedsNoVersionLineInCMake_WhenThatFileIsNotInThePullRequest()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { Files = [.. facts.Files.Where(file => file.Filename != "CMakeLists.txt")] });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Theory]
    [InlineData("chore(main): release 1.10.0", "1.10.0")]
    [InlineData("chore(main): release 12.0.31", "12.0.31")]
    [InlineData("chore(main): release 1.10", null)]
    [InlineData("chore(main): release v1.10.0", null)]
    [InlineData("chore(main): release 1.10.0-rc1", null)]
    [InlineData("chore(main): release 1.10.0 ", null)]
    [InlineData("chore(main): release 1.10.0\nfix: more", null)]
    [InlineData("chore(main): release 1.10.0\n", null)]
    [InlineData("chore: release 1.10.0", null)]
    [InlineData("chore(main): release", null)]
    [InlineData("", null)]
    public void TheVersion_IsTakenFromThePlainReleaseTitle(string title, string? version)
    {
        Assert.Equal(version, LivemapReleaseGate.VersionOf(title));
    }

    public static TheoryData<string, Func<LivemapReleaseFacts, LivemapReleaseFacts>> Refusals => new()
    {
        { "closed", f => f with { Pull = f.Pull with { State = "closed" } } },
        { "merged", f => f with { Pull = f.Pull with { Merged = true } } },
        { "draft", f => f with { Pull = f.Pull with { Draft = true } } },
        { "head repo is another", f => f with { Pull = f.Pull with { HeadRepo = "octo/other" } } },
        { "head repo is gone", f => f with { Pull = f.Pull with { HeadRepo = string.Empty } } },
        { "base repo is another", f => f with { Pull = f.Pull with { BaseRepo = "octo/other" } } },
        { "base is not main", f => f with { Pull = f.Pull with { BaseRef = "release" } } },
        { "head branch is another", f => f with { Pull = f.Pull with { HeadRef = "livemap/build-401" } } },
        { "author is a person", f => f with { Pull = f.Pull with { AuthorLogin = "someone", AuthorType = "User" } } },
        { "author is another bot", f => f with { Pull = f.Pull with { AuthorLogin = "other[bot]" } } },
        { "author named like the bot but a user", f => f with { Pull = f.Pull with { AuthorType = "User" } } },
        { "title is not a release", f => f with { Pull = f.Pull with { Title = "chore(main): bump the dependencies" } } },
        { "title has no title", f => f with { Pull = f.Pull with { Title = string.Empty } } },
        { "the commit has two parents", f => f with { HeadParents = [Main, new string('d', 40)] } },
        { "the commit has no parent", f => f with { HeadParents = [] } },
        { "no files", f => f with { Files = [] } },
        { "a workflow file too", f => f with { Files = [.. f.Files, new LivemapPullRequestFile(".github/workflows/release.yaml", "modified", null, "@@")] } },
        { "a source file too", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("src/main.cpp", "modified", null, "@@")] } },
        { "the builds data too", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("data/builds.json", "modified", null, "@@")] } },
        { "a version file in a folder", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("tools/version.txt", "modified", null, "@@")] } },
        { "a changelog in a folder", f => f with { Files = [new LivemapPullRequestFile("version.txt", "modified", null, "@@"), new LivemapPullRequestFile("docs/CHANGELOG.md", "modified", null, "@@")] } },
        { "version file missing", f => f with { Files = [.. f.Files.Where(file => file.Filename != "version.txt")] } },
        { "version file added", f => f with { Files = [.. f.Files.Select(file => file.Filename == "version.txt" ? file with { Status = "added" } : file)] } },
        { "version file removed", f => f with { Files = [.. f.Files.Select(file => file.Filename == "version.txt" ? file with { Status = "removed" } : file)] } },
        { "changelog renamed in", f => f with { Files = [.. f.Files.Select(file => file.Filename == "CHANGELOG.md" ? file with { PreviousFilename = "OLD.md" } : file)] } },
        { "version file is not the release at the head", f => f with { VersionAtHead = "1.9.9\n" } },
        { "version file is missing at the head", f => f with { VersionAtHead = null } },
        { "version file has a second line", f => f with { VersionAtHead = "1.10.0\n1.11.0\n" } },
        { "version file has a v", f => f with { VersionAtHead = "v1.10.0" } },
        { "version file is empty", f => f with { VersionAtHead = string.Empty } },
        { "cmake patch is missing", f => WithCMake(f, null) },
        { "cmake patch is empty", f => WithCMake(f, string.Empty) },
        { "cmake patch names another project", f => WithCMake(f, Patch.Replace("+project(live_map", "+project(other_map", StringComparison.Ordinal)) },
        { "cmake patch changes the languages", f => WithCMake(f, Patch.Replace("+project(live_map VERSION 1.10.0 LANGUAGES CXX", "+project(live_map VERSION 1.10.0 LANGUAGES C", StringComparison.Ordinal)) },
        { "cmake patch changes the marker", f => WithCMake(f, Patch.Replace("# x-release-please-version\n \n set", "\n \n set", StringComparison.Ordinal)) },
        { "cmake patch sets another version", f => WithCMake(f, Patch.Replace("VERSION 1.10.0", "VERSION 1.11.0", StringComparison.Ordinal)) },
        { "cmake patch adds a line", f => WithCMake(f, Patch + "\n+execute_process(COMMAND sh)") },
        { "cmake patch adds a line before", f => WithCMake(f, "@@ -1 +1,2 @@\n+execute_process(COMMAND sh)\n" + Patch) },
        { "cmake patch removes a line", f => WithCMake(f, Patch + "\n-set(CMAKE_CXX_STANDARD 20)") },
        { "cmake patch is not the project line", f => WithCMake(f, "@@ -1 +1 @@\n-set(A 1)\n+set(A 2)") },
        { "cmake patch only adds", f => WithCMake(f, "@@ -1 +1 @@\n+project(live_map VERSION 1.10.0 LANGUAGES CXX ASM_MASM) # x-release-please-version") },
        { "cmake patch only removes", f => WithCMake(f, "@@ -1 +1 @@\n-project(live_map VERSION 1.9.0 LANGUAGES CXX ASM_MASM) # x-release-please-version") },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void APullRequestThatIsNotAPlainRelease_IsRefused(string why, Func<LivemapReleaseFacts, LivemapReleaseFacts> change)
    {
        var verdict = Evaluate(change(Good()));

        Assert.True(verdict.Outcome == LivemapGateOutcome.Refused, why);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void ARepositoryNameIsComparedWithoutRegardToCase()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { Pull = facts.Pull with { HeadRepo = "OCTO/Livemap", BaseRepo = "Octo/LIVEMAP" } });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void TheReleaseBotNameIsComparedWithoutRegardToCase()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { Pull = facts.Pull with { AuthorLogin = "Release-Bot[bot]" } });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void ACMakeLine_PassesWithAnyTail_WhenBothSidesCarryTheSameOne()
    {
        var verdict = Evaluate(WithCMake(Good(), Patch.Replace("LANGUAGES CXX ASM_MASM", "LANGUAGES C", StringComparison.Ordinal)));

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void ACMakePatchWithANoNewlineNote_StillPasses()
    {
        var verdict = Evaluate(WithCMake(Good(), Patch + "\n\\ No newline at end of file"));

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void NoRequiredChecks_IsRefused_SoAnEmptyListNeverMergesAnything()
    {
        var verdict = LivemapReleaseGate.Evaluate(Good(), []);

        Assert.Equal(LivemapGateOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void ARequiredCheckThatHasNotAppeared_IsPending_AndNamed()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter")] });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Fact]
    public void ARequiredCheckThatFailed_Fails_AndIsNamed()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", conclusion: "failure")] });

        Assert.Equal(LivemapGateOutcome.Failed, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Fact]
    public void ARequiredCheckThatWasSkipped_Fails_SoTheReleaseListNamesNoCheckThatSkipsOnReleases()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", conclusion: "skipped")] });

        Assert.Equal(LivemapGateOutcome.Failed, verdict.Outcome);
    }

    [Fact]
    public void ACheckFromAnotherAppOrAnotherSuite_DoesNotCount()
    {
        var verdict = Evaluate(Good() with
        {
            CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", app: "someone-else"), Run("repo / Build, test, package", suite: 9)],
        });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
    }

    [Fact]
    public void ACheckThatIsNotRequired_IsIgnored_WhateverItsResult()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { CheckRuns = [.. facts.CheckRuns, Run("repo / data-only", conclusion: "skipped")] });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }
}
