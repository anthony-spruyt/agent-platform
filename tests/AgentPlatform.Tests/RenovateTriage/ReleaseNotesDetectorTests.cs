using System.Text.RegularExpressions;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class ReleaseNotesDetectorTests
{
    private const string Versioned = "### [`v1.2.3`](https://redirect.github.com/o/r/releases/tag/v1.2.3)\n\n[Compare Source](https://redirect.github.com/o/r/compare/v1.2.2...v1.2.3)\n\n";
    private const string Older = "### [`v1.2.2`](https://redirect.github.com/o/r/releases/tag/v1.2.2)\n\n";
    private const string TitleOnly = "Python 1.2.3";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", $"release-notes-{name}.txt"));

    private static string Block(string inner, string summary = "o/r (dep)") => $"<details>\n<summary>{summary}</summary>\n\n{inner}\n\n</details>";

    private static string Raw(string summaryLine, string inner, string closer = "</details>") => $"<details>\n{summaryLine}\n\n{inner}\n\n{closer}";

    private static string Row(string dependency, string? source, string to = "1.2.3") =>
        $"| {(source is null ? dependency : $"[{dependency}](https://redirect.github.com/{source})")} | `1.2.2` → `{to}` |";

    private static string Doc(string[] rows, params string[] blocks) =>
        "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n"
        + string.Join("\n", rows)
        + "\n\n---\n\n### Release Notes\n\n"
        + string.Join("\n\n", blocks)
        + "\n\n---\n\n### Configuration\n\n- Branch creation\n  - At any time (no schedule defined)\n- Automerge\n  - At any time (no schedule defined)\n";

    private static string Unclosed(string inner) =>
        $"| Package | Change |\n|---|---|\n{Row("dep", "o/r")}\n\n---\n\n### Release Notes\n\n<details>\n<summary>o/r (dep)</summary>\n\n{inner}";

    private static string Body(params string[] blocks) => Doc([Row("dep", "o/r")], blocks);

    private static string Grouped(params string[] blocks) => Doc([Row("a", "o/a"), Row("b", "o/b")], blocks);

    private static string SourceDoc(int count, string bullet)
    {
        var sources = Enumerable.Range(1, count).Select(i => $"o/r{i}").ToArray();
        return Doc([.. sources.Select(s => Row("dep", s))], [.. sources.Select(s => Block(Versioned + bullet, $"{s} (dep)"))]);
    }

    private static string SameSourceDoc(int count) =>
        Doc([.. Enumerable.Range(1, count).Select(i => Row($"dep{i}", "o/r"))], Block(Versioned + TitleOnly, "o/r (dep1)"));

    [Fact]
    public void Body_WithAVersionSectionHoldingABullet_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture("container-images-2389")));

    [Fact]
    public void Body_WithSeveralBullets_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture("container-images-2290")));

    [Theory]
    [InlineData("container-images-1995")]
    [InlineData("spruyt-labs-3295")]
    public void Body_WhereOnlyOlderVersionsHaveBullets_HasNoNotes(string name) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture(name)));

    [Fact]
    public void Body_WithOnlyATitleLineUnderTheVersion_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture("Chromance-22")));

    [Fact]
    public void Body_WithoutAReleaseNotesSection_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture("repo-operator-926")));

    [Fact]
    public void Body_WhoseNotesUseDotBullets_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture("container-images-186")));

    [Fact]
    public void Body_WhereTwoImagesShareOneBlockOfTheirSourceRepo_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture("spruyt-labs-3552")));

    [Theory]
    [InlineData("repo-operator-949")]
    [InlineData("spruyt-labs-3089")]
    public void Body_WhereAnUpdateHasNoBlock_HasNoNotes(string name) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture(name)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Body_ThatIsEmpty_HasNoNotes(string? body) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(body));

    [Fact]
    public void Body_WithBlocksButNoReleaseNotesHeading_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes($"| Package | Change |\n|---|---|\n{Row("dep", "o/r")}\n\n{Block(Versioned + "- fixed a crash")}"));

    [Fact]
    public void Body_WithTheBlockBeforeTheReleaseNotesHeading_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes($"| Package | Change |\n|---|---|\n{Row("dep", "o/r")}\n\n{Block(Versioned + "- fixed a crash")}\n\n---\n\n### Release Notes\n"));

    [Fact]
    public void Body_WithoutAnUpdateTable_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes($"### Release Notes\n\n{Block(Versioned + "- fixed a crash")}\n"));

    [Fact]
    public void Body_WithALowerCaseReleaseNotesHeading_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")).Replace("### Release Notes", "### release notes", StringComparison.Ordinal)));

    [Theory]
    [InlineData("see ### Release Notes")]
    [InlineData("####### Release Notes")]
    [InlineData("Release Notes")]
    [InlineData(" Release Notes")]
    [InlineData("###Release Notes")]
    [InlineData("### Release Notes and more")]
    public void Body_WhoseReleaseNotesLineIsNotAHeading_HasNoNotes(string line) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")).Replace("### Release Notes", line, StringComparison.Ordinal)));

    [Theory]
    [InlineData("# Release Notes")]
    [InlineData("  ### Release Notes")]
    [InlineData("### Release Notes  ")]
    public void Body_WhoseReleaseNotesHeadingIsIndentedOrPadded_HasNotes(string line) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")).Replace("### Release Notes", line, StringComparison.Ordinal)));

    [Theory]
    [InlineData("- fixed a crash")]
    [InlineData("* fixed a crash")]
    [InlineData("+ fixed a crash")]
    [InlineData("1. fixed a crash")]
    [InlineData("10. fixed a crash")]
    [InlineData("• fixed a crash")]
    [InlineData("•\tfixed a crash")]
    [InlineData("1) fixed a crash")]
    [InlineData("10) fixed a crash")]
    [InlineData("-\tfixed a crash")]
    [InlineData("- 2026")]
    [InlineData("  - fixed a crash")]
    [InlineData("- [#12](https://github.com/o/r/pull/12) fixed a crash")]
    [InlineData("- fix: a crash by @someone in https://github.com/o/r/pull/12")]
    [InlineData("- 修复崩溃")]
    public void Body_WithABulletThatHasText_HasNotes(string bullet) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullet))));

    [Theory]
    [InlineData("- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)")]
    [InlineData("- https://github.com/o/r/pull/12")]
    [InlineData("- <https://github.com/o/r/pull/12>")]
    [InlineData("- http://example.test/x")]
    [InlineData("- [x]()")]
    [InlineData("- [](a b)")]
    [InlineData("- [a][]")]
    [InlineData("- [][b]")]
    [InlineData("* [#12](https://github.com/o/r/pull/12)")]
    [InlineData("- **[v1.2.3](https://github.com/o/r/releases/tag/v1.2.3)**")]
    [InlineData("- ![x](https://example.test/x.png)")]
    [InlineData("- [a][1]")]
    [InlineData("- <br>")]
    [InlineData("- <img src=\"x.png\" alt=\"y\">")]
    [InlineData("- <!-- c -->")]
    [InlineData("- <!-- a > b -->")]
    [InlineData("-")]
    [InlineData("- ")]
    public void Body_WithOnlyLinkOnlyBullets_HasNoNotes(string bullet) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullet))));

    [Theory]
    [InlineData("This release - fixes several crashes")]
    [InlineData("**Full Changelog**: https://github.com/o/r/compare/v1.2.2...v1.2.3")]
    [InlineData("*emphasised* text")]
    [InlineData("-flag")]
    [InlineData("+1 for this")]
    [InlineData("1.2.3 is out")]
    [InlineData(". fixed a crash")]
    [InlineData(") fixed a crash")]
    [InlineData("•fixed a crash")]
    [InlineData("1)fixed a crash")]
    [InlineData("a) fixed a crash")]
    public void Body_WithALineThatIsNotABullet_HasNoNotes(string line) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + line))));

    [Theory]
    [InlineData("firemerge-334")]
    [InlineData("firemerge-385")]
    [InlineData("spruyt-labs-425")]
    [InlineData("spruyt-labs-3206")]
    public void Body_WhoseNewestNotesAreOnlyDependencyOrReleaseBotBullets_HasNoNotes(string name) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture(name)));

    [Theory]
    [InlineData("- Updated dependencies")]
    [InlineData("- Updated dependencies \\[]:")]
    [InlineData("- updated dependencies [abc1234]:")]
    [InlineData("- Updated dependencies [[`abc1234`](https://github.com/o/r/commit/abc1234)]:")]
    [InlineData("- abc1234: Updated dependencies")]
    [InlineData("- [`abc1234`](https://github.com/o/r/commit/abc1234): Updated dependencies")]
    [InlineData("- Thanks @someone! - Updated dependencies")]
    [InlineData("- [#12](https://github.com/o/r/pull/12) [`abc1234`](https://github.com/o/r/commit/abc1234) Thanks [@someone](https://github.com/someone)! - Updated dependencies")]
    [InlineData("- Updated dependencies \\[]:\n  - [@&#8203;o/core](https://github.com/o/core)@&#8203;1.2.3")]
    [InlineData("- Updated dependencies:\n\t- o/core@1.2.3\n    - o/deep@1.2.3")]
    [InlineData("- Updated dependencies\n\n  - o/core@1.2.3\n\n  more text")]
    [InlineData("  - Updated dependencies\n\t- o/core@1.2.3")]
    [InlineData("- Updated dependencies for the new TLS stack")]
    [InlineData("- Release dep-v1.2.3 by [@bot](https://github.com/bot) in [#12](https://github.com/o/r/pull/12)")]
    [InlineData("- Release dep-1.2.3")]
    [InlineData("* sync(charts/dep): v1.2.3 by [@bot](https://github.com/bot) in [#12](https://github.com/o/r/pull/12)")]
    [InlineData("- sync(charts/dep): 1.2.3")]
    [InlineData("* **Full Changelog**: https://github.com/o/r/compare/v1.2.2...v1.2.3")]
    [InlineData("- Full Changelog: <https://github.com/o/r/compare/v1.2.2...v1.2.3>")]
    [InlineData("* @someone made their first contribution in https://github.com/o/r/pull/12")]
    [InlineData("- [@someone](https://github.com/someone) made their first contribution in [#12](https://github.com/o/r/pull/12)")]
    [InlineData("- someone made their first contribution in #12\n- other made her first contribution in #13")]
    [InlineData("- someone made his first contribution in #12")]
    [InlineData("- Thanks @a! - @someone made their first contribution in #12")]
    [InlineData("- ABC1234: Updated dependencies")]
    [InlineData("- Release  dep-v1.2.3\n- sync(charts/dep):  v1.2.3\n- Full  Changelog: x\n- someone made  their  first  contribution in #12")]
    [InlineData("- Updated  dependencies")]
    [InlineData("- Updated&nbsp;dependencies")]
    [InlineData("- &#8203;Updated dependencies")]
    [InlineData("- Updated dependencies\n- Release dep-v1.2.3\n- sync(charts/dep): v1.2.3\n- **Full Changelog**: https://github.com/o/r/compare/a...b")]
    public void Body_WhoseOnlyBulletsAreDependencyBotOrContributorLines_HasNoNotes(string bullets) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullets))));

    [Theory]
    [InlineData("- fixed a crash\n- Updated dependencies\n  - o/core@1.2.3")]
    [InlineData("- Updated dependencies\n  - o/core@1.2.3\n- fixed a crash")]
    [InlineData("- Updated dependencies\n  - o/core@1.2.3\n\nSee the migration guide.\n\n- fixed a crash")]
    [InlineData("  - Updated dependencies\n  - fixed a crash")]
    [InlineData("- Updated dependencies\n  - o/core@1.2.3\n- Updated dependencies\n- fixed a crash")]
    [InlineData("- Release notes are now generated from commits")]
    [InlineData("- Released the embedded client")]
    [InlineData("- Release dep v1.2.3")]
    [InlineData("- sync(charts/dep): fix drift in the values")]
    [InlineData("- sync the cache on startup")]
    [InlineData("- Full changes are listed in the guide")]
    [InlineData("- Several contributors made their first contribution to the docs")]
    [InlineData("- @someone made a contribution in #12")]
    [InlineData("- Thanks @someone! - fixed a crash")]
    [InlineData("- [#12](https://github.com/o/r/pull/12) [`abc1234`](https://github.com/o/r/commit/abc1234) - fix(core): a crash")]
    [InlineData("- abc1234: fixed a crash")]
    [InlineData("- Release 2.0 adds streaming support")]
    [InlineData("- abcdef Updated dependencies in the build image")]
    [InlineData("- Full Changelogs are now signed")]
    [InlineData("- someone made their first contributions page")]
    [InlineData("- aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa: Updated dependencies")]
    [InlineData("- Updated dependencies\n\n## New Contributors\n\n* @someone made their first contribution in #12\n\n- fixed a crash")]
    public void Body_WithARealBulletBesideDependencyBotOrContributorLines_HasNotes(string bullets) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullets))));

    [Fact]
    public void Section_WithOnlyDependencyBumpBullets_HasNoNotes()
    {
        Assert.False(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\n- Updated dependencies\n  - o/core@1.2.3"));
        Assert.True(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\n- Updated dependencies\n  - o/core@1.2.3\n- the crash"));
    }

    [Fact]
    public void Body_WithProseButNoBullets_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "This release fixes several crashes and improves startup time."))));

    [Fact]
    public void Body_WithAnEmptyDetailsBlock_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(string.Empty))));

    [Fact]
    public void Body_WithBulletsButNoVersionHeading_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block("- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsUnderAHeadingThatIsNotAVersionLink_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block("### Changes\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsUnderALinkHeadingThatIsNotAVersion_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block("### [Changes](https://example.test/changes)\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithALinkHeadingThatIsNotAVersionInsideTheNewestSection_KeepsItsBullets() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "### [Changes](https://example.test/changes)\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsOnlyInTheConfigurationSection_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + TitleOnly))));

    [Fact]
    public void Body_WhoseBlockClosesAfterAConfigurationHeading_DoesNotAddItAgain() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(Block(Versioned + "- fixed a crash\n\n## Configuration", "o/a (a)"))));

    [Fact]
    public void Body_WithAConfigurationHeadingInsideTheNotes_KeepsReadingTheBlock() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "### Configuration\n\n- fixed a crash"))));

    [Theory]
    [InlineData("# Configuration", false)]
    [InlineData("## Configuration", false)]
    [InlineData("### Configuration", false)]
    [InlineData("   ### Configuration", false)]
    [InlineData("### Configuration  ", false)]
    [InlineData("### Configuration\r", false)]
    [InlineData("    ### Configuration", true)]
    [InlineData("#### Configuration", true)]
    [InlineData("###### Configuration", true)]
    [InlineData("###Configuration", true)]
    [InlineData("### Configuration options", true)]
    [InlineData("see ### Configuration", true)]
    public void Body_WithABlockAfterAConfigurationLine_ReadsItOnlyWhenTheLineIsNotThatHeading(string line, bool hasNotes)
    {
        var late = Block(Versioned + "- fixed a crash", "o/b (b)");
        var body = Grouped(Block(Versioned + "- fixed a crash", "o/a (a)")).Replace("### Configuration", line, StringComparison.Ordinal) + "\n" + late + "\n";

        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(body));
    }

    [Theory]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("####")]
    [InlineData("######")]
    public void Body_WhoseOnlyVersionHeadingIsNotRenovatesLevel_HasNoNotes(string level) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned.Replace("###", level, StringComparison.Ordinal) + "- fixed a crash"))));

    [Fact]
    public void Body_WhoseVersionHeadingIsNotAVersionNumber_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture("spruyt-labs-2689")));

    [Theory]
    [InlineData("vertical-pod-autoscaler-1.2.3", "vertical-pod-autoscaler-1.2.3", true)]
    [InlineData("vertical-pod-autoscaler-1.2.3", "vertical-pod-autoscaler-1.2.2", false)]
    [InlineData("release-1.2.3", "v1.2.3", false)]
    [InlineData("1.2.3", "release-1.2.3", false)]
    public void Body_WhoseVersionHeadingHoldsAWordAndAVersion_IsTheNewVersionOnlyWhenItIsTheSameText(string to, string heading, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", to)], Block(Versioned.Replace("v1.2.3", heading, StringComparison.Ordinal) + "- fixed a crash"))));

    [Theory]
    [InlineData("### [`v1.0.0`](https://example.test/x)", false)]
    [InlineData("### [`1.0.0`](https://example.test/x)", false)]
    [InlineData("### [`V1.0.0`](https://example.test/x)", false)]
    [InlineData("### [`vertical-pod-1.0.0`](https://example.test/x)", false)]
    [InlineData("### [`v1.0.0`](https://example.test/x): Some release name", false)]
    [InlineData("### [`v1.0.0`](https://example.test/x y)", false)]
    [InlineData("### [`v1.0.0`](", false)]
    [InlineData("### [`stable`](https://example.test/x)", false)]
    [InlineData("#### [1.0.0](https://example.test/x)", true)]
    [InlineData("#### [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("## [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("####### [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("### [1.0.0](https://example.test/x)", true)]
    [InlineData("### [v1.0.0](https://example.test/x)", true)]
    [InlineData("### [``](https://example.test/x)", true)]
    [InlineData("###  [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("###[`v1.0.0`](https://example.test/x)", true)]
    [InlineData(" ### [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("   ### [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("see ### [`v1.0.0`](https://example.test/x)", true)]
    [InlineData("### [`v1.0.0`]", true)]
    [InlineData("### [`v1.0.0`] (https://example.test/x)", true)]
    [InlineData("[`v1.0.0`](https://example.test/x)", true)]
    [InlineData("### [Release v1.0.0](https://example.test/x)", true)]
    public void Body_WithAnotherLineInTheNewestSection_StartsAnOlderSectionOnlyForRenovatesHeading(string line, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + $"{line}\n\n- fixed a crash"))));

    [Fact]
    public void Body_WhereTheBulletIsOnlyUnderAnOlderVersionSection_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "\n\n" + Older + "- fixed a crash"))));

    [Fact]
    public void Body_WhereTheNewestVersionSectionHasABulletAndOlderOnesDoNot_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash\n\n" + Older + "prose"))));

    [Fact]
    public void Body_WithSubHeadingsUnderTheVersion_ReadsTheBulletsBelowThem() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "##### Bug Fixes\n\n* fixed a crash"))));

    [Fact]
    public void Body_WithBulletsOnlyInsideACodeFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```yaml\n- fixed a crash\n```"))));

    [Fact]
    public void Body_WithSeveralBulletsInsideACodeFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```yaml\n- fixed a crash\n- fixed another\n```"))));

    [Fact]
    public void Body_WithBulletsInsideAnIndentedFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "  ```\n- fixed a crash\n  ```"))));

    [Fact]
    public void Body_WithFenceMarkersMidLine_DoesNotStartACodeFence() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "Use ``` to fence code\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsOnlyInsideATildeFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "~~~\n- fixed a crash\n~~~"))));

    [Fact]
    public void Body_WithAVersionHeadingInsideACodeFence_KeepsTheNewestSectionOpen() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```md\n" + Older + "```\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsInsideAMultiLineComment_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<!--\n- fixed a crash\n-->"))));

    [Theory]
    [InlineData("Run this:\n\n    - fixed a crash")]
    [InlineData("Run this:\n\n    - fixed a crash\n    - fixed another")]
    [InlineData("Run this:\n\n\t- fixed a crash")]
    [InlineData("Run this:\n\n  \t- fixed a crash")]
    [InlineData("    - fixed a crash")]
    [InlineData("Run this:\n\n    echo hi\n    - fixed a crash")]
    [InlineData("Run this:\n\n    echo hi\n\n    - fixed a crash")]
    [InlineData("Run this:\n\n     - fixed a crash")]
    [InlineData("Run this:\n\n    1. fixed a crash")]
    [InlineData("Run this:\n```\necho hi\n```\n    - fixed a crash")]
    [InlineData("- Updated dependencies\n\nSee the migration guide.\n\n    - fixed a crash")]
    public void Body_WithBulletsOnlyInsideAnIndentedCodeBlock_HasNoNotes(string notes) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + notes))));

    [Theory]
    [InlineData("Run this:\n    - fixed a crash")]
    [InlineData("Run this:\n\n   - fixed a crash")]
    [InlineData("    echo hi\n\n- fixed a crash")]
    [InlineData("    echo hi\n\nUse it.\n\n- fixed a crash")]
    [InlineData("    echo hi\n- fixed a crash")]
    [InlineData("- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)\n\n    - fixed a crash")]
    [InlineData("- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)\n    continued\n\n    - fixed a crash")]
    [InlineData("1. [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)\n\n    * fixed a crash")]
    [InlineData("- <br>\n\n    - fixed a crash")]
    [InlineData("Run this:\n\n  - [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)\n\n      - fixed a crash")]
    [InlineData("- [link](https://x.y)\n    - Updated dependencies\n   \t- o/core@1.2.3")]
    public void Body_WithABulletThatIsNotInAnIndentedCodeBlock_HasNotes(string notes) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + notes))));

    [Fact]
    public void Body_WithAnIndentedCodeBlockAfterAList_ReadsTheListBeforeIt() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash\n\nRun this:\n\n    - not a bullet"))));

    [Fact]
    public void Section_WithBulletsOnlyInsideAnIndentedCodeBlock_HasNoNotes()
    {
        Assert.False(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\nRun this:\n\n    - the crash"));
        Assert.True(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\nRun this:\n\n    - the crash\n\n- the other crash"));
    }

    [Fact]
    public void Section_StartingWithAnIndentedBullet_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.SectionHasNotes("    - the crash"));

    [Fact]
    public void Body_WithBulletsAfterAMultiLineComment_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<!--\nnotes\n--> text\n- fixed a crash"))));

    [Fact]
    public void Body_WithACommentThatNeverCloses_HidesTheRest() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<!-- draft\n- fixed a crash"))));

    [Fact]
    public void Body_WithACommentOpenerInsideACodeFence_DoesNotHideTheBulletsAfterIt() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```html\n<!--\n```\n\n- fixed a crash"))));

    [Fact]
    public void Body_WhoseLastBlockEndsTheText_HasNotes()
    {
        var body = Body(Block(Versioned + "- fixed a crash"));

        Assert.True(ReleaseNotesDetector.BodyHasNotes(body[..(body.IndexOf("</details>", StringComparison.Ordinal) + "</details>".Length)]));
    }

    [Fact]
    public void Body_EndingInAnOpeningTag_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes($"| Package | Change |\n|---|---|\n{Row("dep", "o/r")}\n\n### Release Notes\n\n<details>"));

    [Fact]
    public void Body_WithADetailsTagInsideACodeFence_StillClosesItsBlock() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash\n\n```html\n<details>\n```"))));

    [Fact]
    public void Body_WithABlockStartInsideACodeFence_DoesNotCutTheBlockOff() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash\n\n```html\n<details>\n<summary>o/r (dep)</summary>\n```"))));

    [Fact]
    public void Body_WithAFenceMarkerInsideAComment_DoesNotStartACodeFence() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<!--\n```\n-->\n- fixed a crash"))));

    [Fact]
    public void Body_WithANestedBlockThatHasNoSummary_ReadsTheBulletsInsideIt() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<details>\n\n- fixed a crash\n\n</details>"))));

    [Fact]
    public void Body_WithABlockSummaryThatIsNotARepoName_IsIgnored() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash", "Show more details"))));

    [Fact]
    public void Body_WithANestedDetailsBlock_ReadsTheBulletsInsideIt() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<details>\n<summary>More</summary>\n\n- fixed a crash\n\n</details>"))));

    [Fact]
    public void Body_WithBulletsAfterANestedDetailsBlockClosed_StillReadsTheOuterBlock() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<details>\n<summary>More</summary>\n\nprose\n\n</details>\n\n- fixed a crash"))));

    [Theory]
    [InlineData("<details>", "</details>")]
    [InlineData("  <details>", "  </details>")]
    [InlineData("<details open>", "</details>")]
    [InlineData("<details>", "</details >")]
    public void Body_WithANestedBlockBeforeTheNotes_ReadsTheNotesAfterIt(string open, string close) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + $"{open}\n<summary>More</summary>\n\nmore\n\n{close}\n\n---\n\n- fixed a crash"))));

    [Theory]
    [InlineData("Use the <details> tag")]
    [InlineData("<detailsx>")]
    [InlineData("see </details>")]
    [InlineData("</details> and more")]
    public void Body_WithATagInsideALineOfNotes_KeepsReadingTheBlock(string line) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + $"{line}\n\n---\n\n- fixed a crash"))));

    [Theory]
    [InlineData("<summary>o/r (dep)</summary>", true)]
    [InlineData("  <summary>o/r (dep)</summary>  ", true)]
    [InlineData("text <summary>o/r (dep)</summary>", false)]
    [InlineData("<summary>o/r (dep)</summary> text", false)]
    public void Body_WhoseSummaryLineHasOtherText_IsIgnored(string summaryLine, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Body(Raw(summaryLine, Versioned + "- fixed a crash"))));

    [Theory]
    [InlineData("o/r (dep)", true)]
    [InlineData("o/r", true)]
    [InlineData("Show o/r", false)]
    [InlineData("o/r more", false)]
    [InlineData("o/r (dep) more", false)]
    public void Body_WhoseSummaryHasMoreThanARepoName_IsIgnored(string title, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash", title))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Body_WithBlankLinesBetweenDetailsAndSummary_HasNotes(string gap) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body($"<details>\n{gap}\n<summary>o/r (dep)</summary>\n\n{Versioned}- fixed a crash\n\n</details>")));

    [Fact]
    public void Body_WithASummaryButNoDetailsTag_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body("<summary>o/r (dep)</summary>\n\n" + Versioned + "- fixed a crash\n\n</details>")));

    [Theory]
    [InlineData("---", false)]
    [InlineData("----", false)]
    [InlineData("   ---", false)]
    [InlineData("---   ", false)]
    [InlineData("--", true)]
    [InlineData("    ---", true)]
    [InlineData("--- and more", true)]
    [InlineData("and ---", true)]
    public void Body_WithAClosingTagInTheNotes_EndsTheBlockOnlyWhenARuleFollows(string line, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + $"intro\n</details>\n\n{line}\n\n- fixed a crash"))));

    [Fact]
    public void Body_WhoseDetailsBlockNeverClosesBeforeTheConfigurationFooter_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")[..^"</details>".Length])));

    [Fact]
    public void Body_WhoseDetailsBlockNeverCloses_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + "- fixed a crash")));

    [Theory]
    [InlineData("container-images-1567")]
    [InlineData("container-images-506")]
    public void Body_CutOffByRenovateInsideNestedNotes_HasNotes(string name) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture(name)));

    [Theory]
    [InlineData("> PR body was truncated to here.")]
    [InlineData("> \u2702 PR body was truncated to here.")]
    [InlineData("> \u2702\uFE0F PR body was truncated to here.")]
    [InlineData("PR body was truncated to here")]
    [InlineData("   >   PR body was truncated to here.   ")]
    [InlineData("> PR body was truncated to here.\r")]
    public void Body_CutOffAtARenovateTruncationMarker_KeepsTheNotesBeforeIt(string marker) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + $"- fixed a crash\n\n<details><summary>3 commits</summary>\n\n- more\n\n```\ncode\n\n{marker}\n\n</details>")));

    [Theory]
    [InlineData("> PR body was truncated to here and more")]
    [InlineData("> the PR body was truncated to here.")]
    [InlineData("> PR body was truncated.")]
    [InlineData("- PR body was truncated to here.")]
    [InlineData("> pr body was truncated to here.")]
    public void Body_CutOffAtALineThatIsNotTheTruncationMarker_HasNoNotes(string line) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + $"- fixed a crash\n\n<details><summary>3 commits</summary>\n\n{line}\n\n</details>")));

    [Theory]
    [InlineData("### Configuration", true)]
    [InlineData("## Configuration", true)]
    [InlineData("# Configuration", true)]
    [InlineData("   ### Configuration  ", true)]
    [InlineData("    ### Configuration", false)]
    [InlineData("#### Configuration", false)]
    [InlineData("### Configuration options", false)]
    [InlineData("see ### Configuration", false)]
    public void Body_CutOffAtAConfigurationHeading_KeepsTheNotesBeforeItOnlyForThatHeading(string line, bool hasNotes) =>
        Assert.Equal(hasNotes, ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + $"- fixed a crash\n\n```\ncode\n\n<details><summary>3 commits</summary>\n\n{line}\n\n- never read")));

    [Fact]
    public void Body_CutOffInsideAnOpenFence_KeepsTheNotesBeforeTheConfigurationHeading() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + "- fixed a crash\n\n```\ncode\n\n---\n\n### Configuration\n")));

    [Fact]
    public void Body_CutOffBeforeAnyNotes_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + "intro\n\n> PR body was truncated to here.\n\n- listed after the cut")));

    [Fact]
    public void Body_CutOffTwice_ReadsOnlyUpToTheFirstCut() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Unclosed(Versioned + "intro\n\n### Configuration\n\n- listed after the cut\n\n> PR body was truncated to here.")));

    [Fact]
    public void Body_WhereACutOffBlockIsFollowedByAnotherThatNeverCloses_HasNoNotes()
    {
        var first = $"<details>\n<summary>o/a (a)</summary>\n\n{Versioned}- fixed a crash\n\n### Configuration\n\nmore";
        var second = $"<details>\n<summary>o/b (b)</summary>\n\n{Versioned}- fixed a crash";
        var table = Grouped().Split("### Release Notes", 2)[0];

        Assert.False(ReleaseNotesDetector.BodyHasNotes($"{table}### Release Notes\n\n{first}\n\n{second}"));
    }

    [Fact]
    public void Body_WhereABlockIsCutOffByTheNextOne_HasNoNotes()
    {
        var cut = Block(Versioned + "- fixed a crash", "o/a (a)")[..^"</details>".Length];

        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(cut, Block(Versioned + "- fixed a crash", "o/b (b)"))));
    }

    [Fact]
    public void Body_WithWindowsLineEndings_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")).Replace("\n", "\r\n", StringComparison.Ordinal)));

    [Fact]
    public void Body_OfAGroupedPr_HasNotesOnlyWhenEveryUpdateHasABlockWithNotes()
    {
        var noted = Block(Versioned + "- fixed a crash", "o/a (a)");
        var titleOnly = Block(Versioned + TitleOnly, "o/b (b)");

        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(noted, titleOnly)));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(titleOnly, noted)));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(noted)));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(Block(Versioned + "- fixed a crash", "o/b (b)"))));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Grouped(noted, Block(Versioned + "* fixed another crash", "o/b (b)"))));
    }

    [Fact]
    public void Body_WhereTwoUpdatesShareASourceAndANewVersion_NeedsOnlyOneBlock() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("a", "o/r"), Row("b", "o/r")], Block(Versioned + "- fixed a crash", "o/r (a)"))));

    [Fact]
    public void Body_WhereTwoUpdatesShareASourceButNotTheNewVersion_NeedsABlockForEach() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("a", "o/r"), Row("b", "o/r", "1.2.4")], Block(Versioned + "- fixed a crash", "o/r (a)"))));

    [Fact]
    public void Body_WithABlockThatMatchesNoUpdate_IsIgnored()
    {
        var other = Block(Versioned + "- fixed a crash", "o/x (x)");

        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(other)));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + TitleOnly), other)));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(other, Block(Versioned + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhereTheSourceRepoDiffersInCase_StillMatchesItsBlock() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "Owner/Repo")], Block(Versioned + "- fixed a crash", "owner/repo (dep)"))));

    [Fact]
    public void Body_WhereAnUpdateHasNoSource_MatchesItsBlockByDependency()
    {
        var rows = new[] { Row("dep-b", null) };

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc(rows, Block(Versioned + "- fixed a crash", "o/b (dep-b)"))));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc(rows, Block(Versioned + "- fixed a crash", "dep-b"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc(rows, Block(Versioned + "- fixed a crash", "o/b (other)"))));
    }

    [Theory]
    [InlineData("^1.0.0")]
    [InlineData("~1.2.0")]
    [InlineData("~>1.2")]
    [InlineData("~> 1.2")]
    [InlineData("1.2.3-alpine")]
    [InlineData("1.2.3-alpine-slim")]
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("v1")]
    public void Body_WhereTheNewVersionIsARangeOrAFloatingTagThatCoversTheNewestSection_HasNotes(string to) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", to)], Block(Versioned + "- fixed a crash"))));

    [Theory]
    [InlineData("1.2.4")]
    [InlineData("^1.3.0")]
    [InlineData("~> 1.3")]
    [InlineData("^9.0.0")]
    [InlineData(">=1.2,<1.3")]
    [InlineData(">=1.2 <1.3")]
    [InlineData("<2")]
    [InlineData("*")]
    [InlineData("1.2.3-alpine", "1.2.4")]
    [InlineData("1.2-alpine")]
    [InlineData("1-slim")]
    [InlineData("1.3")]
    [InlineData("10")]
    [InlineData("latest")]
    [InlineData("stable")]
    public void Body_WhereTheNewVersionIsNotCoveredByTheNewestSection_HasNoNotes(string to, string heading = "1.2.3") =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", to)], Block(Versioned.Replace("v1.2.3", heading, StringComparison.Ordinal) + "- fixed a crash"))));

    [Theory]
    [InlineData("firemerge-478")]
    [InlineData("SunGather-423")]
    [InlineData("spruyt-labs-3291")]
    [InlineData("spruyt-labs-2233")]
    public void Body_WhoseNewVersionIsARangeFloatingOrSuffixedTag_HasNotes(string name) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Fixture(name)));

    [Fact]
    public void Body_WhoseNotesSkipTheNewVersion_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Fixture("container-images-443")));

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("v1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("==1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("=1.2.3", "1.2.4", false)]
    [InlineData("==1.2.3", "1.2.4", false)]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.1", false)]
    [InlineData("^1.2.3", "1.2.4", true)]
    [InlineData("^1.2.3", "0.0.1", false)]
    [InlineData("^1.3.0", "1.2.6", false)]
    [InlineData(">=1.3.0", "0.0.1", false)]
    [InlineData("~> 0.79", "0.78.3", false)]
    [InlineData("^9.0.0", "0.0.1", false)]
    [InlineData("^20.0.0", "20.1.1", true)]
    [InlineData("~> 0.81", "0.81.2", true)]
    [InlineData("^1.2", "1.2.0", true)]
    [InlineData("^1.2.0", "1.10.0", true)]
    [InlineData("^1.10.0", "1.9.0", false)]
    [InlineData("^1.02.0", "1.2.0", true)]
    [InlineData("^1.0.0", "1.0", true)]
    [InlineData("^1.0.1", "1.0", false)]
    [InlineData("^1", "1.0.0", true)]
    [InlineData("^1.0", "1.99999999999999999999", true)]
    [InlineData("^1.99999999999999999999", "1.99999999999999999998", false)]
    [InlineData(">= 1.2", "1.2.5", true)]
    [InlineData("> 1", "2", true)]
    [InlineData("^1.2.3", "1.2.3-rc.1", false)]
    [InlineData("^1.2.3", "1.3.x", false)]
    [InlineData("^1.2.3", "^1.2.3", false)]
    [InlineData("^1.2.3", "", false)]
    [InlineData("^1.2.x", "1.2.5", false)]
    [InlineData("^1.2.3 ", "1.2.4", true)]
    [InlineData("~1.2.0", "1.2.9", true)]
    [InlineData("~>0.81", "0.81.4", true)]
    [InlineData("~> 0.81", "0.82.0", true)]
    [InlineData(">1", "2", true)]
    [InlineData("= 1.2", "1.2.5", false)]
    [InlineData("= 1.3", "1.2.5", false)]
    [InlineData(">1.2.3", "1.2.3", false)]
    [InlineData(">1.2.3", "1.2.3.0", false)]
    [InlineData(">1.2.3", "1.2.2", false)]
    [InlineData(">1.2.3", "1.2.4", true)]
    [InlineData("> 1.2.3", "1.3.0", true)]
    [InlineData("= 1.2.0", "1.2.5", false)]
    [InlineData("= 1.2.0", "1.1.9", false)]
    [InlineData("= 1.2.0", "1.2.0", true)]
    [InlineData("= 1.2", "1.2", true)]
    [InlineData(">=1.2.3", "1.2.3", true)]
    [InlineData(">=1.2.3", "1.2.2", false)]
    [InlineData("~1.2.0", "1.2.0", true)]
    [InlineData("~> 0.81", "0.81.0", true)]
    [InlineData("<2", "1.9.0", false)]
    [InlineData("<=2", "1.9.0", false)]
    [InlineData("<2.0.0", "0.0.1", false)]
    [InlineData("*", "1.2.3", false)]
    [InlineData(">=0.13,<0.14", "0.13.0", false)]
    [InlineData(">=1.2 <1.3", "1.2.5", false)]
    [InlineData("1.0,2.0", "2.0", false)]
    [InlineData("1.0 - 2.0", "2.0", false)]
    [InlineData("1.0|2.0", "2.0", false)]
    [InlineData("1.0 || 2.0", "2.0", false)]
    [InlineData("^1.0.0 || ^2.0.0", "2.1.0", false)]
    [InlineData("1.^2", "9.9.9", false)]
    [InlineData("a<b", "9.9.9", false)]
    [InlineData("a>b", "9.9.9", false)]
    [InlineData("a~b", "9.9.9", false)]
    [InlineData("a*b", "9.9.9", false)]
    [InlineData("1.2.3-alpine", "1.2.3", true)]
    [InlineData("10.0.13-noble-chiseled", "10.0.13", true)]
    [InlineData("1.2.3-alpine", "1.2.4", false)]
    [InlineData("1.2.3-alpine", "1.2.3-alpine", true)]
    [InlineData("1.2.3-alpine", "1.2.3.1", false)]
    [InlineData("1.2.3+build", "1.2.3", false)]
    [InlineData("24-slim", "24.15.0", false)]
    [InlineData("1.2-alpine", "1.2.0", false)]
    [InlineData("1.2-alpine", "1.2", true)]
    [InlineData("3.15", "3.15.0", true)]
    [InlineData("3.15", "3.15.7", true)]
    [InlineData("3.15", "3.15", true)]
    [InlineData("v7", "7.0.0", true)]
    [InlineData("7", "7.1.2", true)]
    [InlineData("3.15", "3.150.0", false)]
    [InlineData("3.15", "3.16.0", false)]
    [InlineData("7", "70.0.0", false)]
    [InlineData("7", "8.0.0", false)]
    [InlineData("1.2.3", "1.2.3.9", false)]
    [InlineData("1.2.3-rc.1", "1.2.3", false)]
    [InlineData("1.2.3-rc1", "1.2.3", false)]
    [InlineData("1.2.3-RC.1", "1.2.3", false)]
    [InlineData("1.2.3-alpha", "1.2.3", false)]
    [InlineData("1.2.3-alpha.2", "1.2.3", false)]
    [InlineData("1.2.3-beta.2", "1.2.3", false)]
    [InlineData("1.2.3-pre", "1.2.3", false)]
    [InlineData("1.2.3-dev", "1.2.3", false)]
    [InlineData("1.2.3-preview.1", "1.2.3", false)]
    [InlineData("1.2.3-canary.5", "1.2.3", false)]
    [InlineData("1.2.3-next.2", "1.2.3", false)]
    [InlineData("1.2.3-rc.1.2", "1.2.3", false)]
    [InlineData("1.2.3-beta.hotfix", "1.2.3", false)]
    [InlineData("1.2.3-rc-alpine", "1.2.3", false)]
    [InlineData("1.2.3-rc_x", "1.2.3", false)]
    [InlineData("1.2.3-SNAPSHOT", "1.2.3", false)]
    [InlineData("1.2.3-M1", "1.2.3", false)]
    [InlineData("1.2.3-nightly", "1.2.3", false)]
    [InlineData("21-ea", "21", false)]
    [InlineData("1.2.3-alpine-ea", "1.2.3", false)]
    [InlineData("1.2.3-ea.1", "1.2.3", false)]
    [InlineData("1.2.3-alpine-M2", "1.2.3", false)]
    [InlineData("1.2.3-alpine.ea", "1.2.3", false)]
    [InlineData("1.2.3-alpine.M1", "1.2.3", false)]
    [InlineData("21-EA", "21", false)]
    [InlineData("1.2.3-bookworm", "1.2.3", true)]
    [InlineData("1.2.3-early", "1.2.3", true)]
    [InlineData("1.2.3-tea", "1.2.3", true)]
    [InlineData("1.2.3-arm64", "1.2.3", true)]
    [InlineData("1.2.3-alpine-rc.1", "1.2.3", false)]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1", true)]
    [InlineData("1.2.3-slim", "1.2.3", true)]
    [InlineData("1.2-rc.1", "1.2.0", false)]
    [InlineData("1.2", "1.2.0-rc.1", false)]
    [InlineData("1.2", "1.2.3+build", false)]
    [InlineData("1.2", "1.2.x", false)]
    [InlineData("1", "1.0.0-beta", false)]
    [InlineData("1.2.3", "1.2.3-alpine", false)]
    [InlineData("latest", "1.2.3", false)]
    [InlineData("main", "main2", false)]
    [InlineData("abc1234", "1.2.3", false)]
    [InlineData("vertical-1.2.3", "vertical-1.2.3", true)]
    [InlineData("vertical-1.2.3", "vertical-1.2.4", false)]
    [InlineData(" 1.2.3 ", "1.2.4", false)]
    [InlineData("1.2.3 ", "1.2.3", true)]
    [InlineData("1_beta", "1.5.0", false)]
    [InlineData("  ", ".5", false)]
    [InlineData("1.0.0-dev", "1.0.0-dev", true)]
    [InlineData("a-1", "1", false)]
    public void CoversVersion_AcceptsTheNewestSectionOnlyWhenItIsTheNewVersionOrTheRangeOrTagCoversIt(string to, string newest, bool covers) =>
        Assert.Equal(covers, ReleaseNotesDetector.CoversVersion(to, newest.TrimStart('v')));

    [Theory]
    [InlineData("^1.0.0", "1.3.5", "1.3.5", true)]
    [InlineData("^1.0.0", "v1.3.5", "1.3.5", true)]
    [InlineData("^1.0.0", "1.3.5", "1.3.4", false)]
    [InlineData("^1.0.0", "1.3.5", "1.4.0", false)]
    [InlineData("^1.0.0", "1.3.5", "1.3.50", false)]
    [InlineData("^1.0.0", "1.3.5", "1.0.0", false)]
    [InlineData("^1.0.0", "1.3.5", "", false)]
    [InlineData("^1.0.0", "1.3.5-rc.1", "1.3.5", false)]
    [InlineData("~> 0.79", "0.79.2", "0.79.2", true)]
    [InlineData("~> 0.79", "0.79.2", "0.80.0", false)]
    [InlineData(">=0.13,<0.14", "0.13.1", "0.13.1", true)]
    [InlineData(">=0.13,<0.14", "0.13.1", "0.13.0", false)]
    [InlineData("<2", "1.9.0", "1.9.0", true)]
    [InlineData("*", "1.2.3", "1.2.3", true)]
    [InlineData("^1.0.0 || ^2.0.0", "2.1.0", "2.1.0", true)]
    [InlineData("^1.0.0 || ^2.0.0", "2.1.0", "1.0.0", false)]
    [InlineData("^1.2.3", "9.9.9", "1.2.3", false)]
    public void CoversVersion_WhenTheRowShowsTheResolvedVersion_AcceptsOnlyAHeadingEqualToIt(string to, string resolved, string newest, bool covers) =>
        Assert.Equal(covers, ReleaseNotesDetector.CoversVersion(to, newest, resolved));

    [Fact]
    public void Body_WhereTheDiffsLinkAndTheBadgeDifferOnlyByAVPrefix_NeedsTheNewestSectionToBeThatVersion()
    {
        static string Linked() =>
            "| [dep](https://redirect.github.com/o/r) | [`^1.0.0` → `^1.2.0`](https://renovatebot.com/diffs/npm/dep/1.0.0/v1.2.3) | ![age](https://developer.mend.io/api/mc/badges/age/npm/dep/1.2.3?slim=true) |";

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Linked()], Block(Versioned + "- fixed a crash"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Linked()], Block(Versioned.Replace("v1.2.3", "v1.2.4", StringComparison.Ordinal) + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhereTheRowShowsTheResolvedVersion_NeedsTheNewestSectionToBeThatVersion()
    {
        static string Linked(string resolved) =>
            $"| [dep](https://redirect.github.com/o/r) | [`^1.0.0` → `^1.2.0`](https://renovatebot.com/diffs/npm/dep/1.0.0/{resolved}) | ![age](https://developer.mend.io/api/mc/badges/age/npm/dep/{resolved}?slim=true) |";

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Linked("1.2.3")], Block(Versioned + "- fixed a crash"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Linked("1.2.4")], Block(Versioned + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhereTheRowShowsOnlyAnAgeBadge_NeedsTheNewestSectionToBeThatVersion()
    {
        static string Badged(string resolved) =>
            $"| [dep](https://redirect.github.com/o/r) | `>=1.0,<2.0` → `>=1.2,<2.0` | ![age](https://developer.mend.io/api/mc/badges/age/npm/dep/{resolved}?slim=true) |";

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Badged("1.2.3")], Block(Versioned + "- fixed a crash"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Badged("1.2.4")], Block(Versioned + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhereAnUpdateHasASource_MatchesItsBlockOnlyBySource() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash", "o/other (dep)"))));

    [Fact]
    public void Body_WhereTheBlockSummaryEscapesTheDependencyName_StillMatchesIt() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("@scope/pkg", null)], Block(Versioned + "- fixed a crash", "o/b (@&#8203;scope/pkg)"))));

    [Fact]
    public void Body_WhoseNewestVersionSectionIsNotTheNewVersion_HasNoNotes()
    {
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Older + "- fixed a crash"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Older + "prose\n\n" + Versioned + "- fixed a crash"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "1.2.4")], Block(Versioned + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhoseNewestVersionSectionIsTheNewVersionUpToItsVPrefix_HasNotes()
    {
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "v1.2.3")], Block(Versioned + "- fixed a crash"))));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "==1.2.3")], Block(Versioned + "- fixed a crash"))));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "1.2.3")], Block(Versioned.Replace("`v1.2.3`", "`1.2.3`", StringComparison.Ordinal) + "- fixed a crash"))));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "1.2.3")], Block(Versioned.Replace("`v1.2.3`", "`V1.2.3`", StringComparison.Ordinal) + "- fixed a crash"))));
    }

    [Fact]
    public void Body_WhoseNewestVersionSectionDiffersFromTheNewVersionInCase_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", "1.2.3-RC1")], Block(Versioned.Replace("`v1.2.3`", "`v1.2.3-rc1`", StringComparison.Ordinal) + "- fixed a crash"))));

    [Fact]
    public void Body_WhereTheDependencyNameDiffersInCase_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("Dep-B", null)], Block(Versioned + "- fixed a crash", "o/b (dep-b)"))));

    [Fact]
    public void Body_WithAClosingTagInsideInlineCode_KeepsReadingTheBlock()
    {
        var first = Block(Versioned + "Fixed the `</details>` tag\n\n---\n\n- fixed a crash\n\n" + Older + "- older", "o/a (a)");

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + "- fixed a crash", "o/b (b)"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + TitleOnly, "o/b (b)"))));
    }

    [Fact]
    public void Body_WithAClosingTagOnItsOwnLineInTheNotes_KeepsReadingTheBlock()
    {
        var first = Block(Versioned + "intro\n</details>\n- fixed a crash\n\n" + Older + "- older", "o/a (a)");

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + "- fixed a crash", "o/b (b)"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + TitleOnly, "o/b (b)"))));
    }

    [Fact]
    public void Body_WithAClosingTagInsideACodeFence_KeepsReadingTheBlock()
    {
        var first = Block(Versioned + "```html\n</details>\n---\n```\n\n- fixed a crash\n\n" + Older + "- older", "o/a (a)");

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + "- fixed a crash", "o/b (b)"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + TitleOnly, "o/b (b)"))));
    }

    [Fact]
    public void Body_WithAClosingTagFollowedByARuleInTheNotes_StillChecksTheLaterBlocks()
    {
        var first = Block(Versioned + "- fixed a crash\n</details>\n\n---\n\nmore notes\n\n" + Older + "- older", "o/a (a)");

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + "- fixed a crash", "o/b (b)"))));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Grouped(first, Block(Versioned + TitleOnly, "o/b (b)"))));
    }

    [Fact]
    public void Body_WithAHugeBulletOfRepeatedCommentOpeners_IsDecidedWithoutAMatchTimeout()
    {
        var bullet = "- fixed a crash " + string.Concat(Enumerable.Repeat("<!--", 40_000)) + "-->";

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullet))));
        Assert.True(ReleaseNotesDetector.Found(Body(Block(Versioned + bullet)), null));
    }

    [Fact]
    public void Text_WithAHugeBulletOfRepeatedLinkOpeners_IsDecidedWithoutAMatchTimeout()
    {
        var bullet = "- fixed a crash " + new string('[', 400_000) + "]";

        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullet))));
        Assert.True(ReleaseNotesDetector.SectionHasNotes(bullet));
    }

    [Fact]
    public void AScanThatTimesOut_FindsNoNotes() =>
        Assert.False(ReleaseNotesDetector.Guarded(() => throw new RegexMatchTimeoutException()));

    [Fact]
    public void AScanThatFinishes_KeepsItsAnswer()
    {
        Assert.True(ReleaseNotesDetector.Guarded(() => true));
        Assert.False(ReleaseNotesDetector.Guarded(() => false));
    }

    [Fact]
    public void Section_WithABullet_HasNotes() =>
        Assert.True(ReleaseNotesDetector.SectionHasNotes("## [1.2.3] - 2026-01-01\n\n### Fixed\n\n- the crash"));

    [Fact]
    public void Section_WithoutABullet_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\n" + TitleOnly));

    [Theory]
    [InlineData("## 1.2.3\n\n- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)")]
    [InlineData("## 1.2.3\n\n* https://github.com/o/r/pull/12")]
    [InlineData("## 1.2.3\n\n```\n- the crash\n```")]
    [InlineData("## 1.2.3\n\n<!--\n- the crash\n-->")]
    [InlineData("")]
    [InlineData(null)]
    public void Section_WithOnlyLinksOrNoText_HasNoNotes(string? section) =>
        Assert.False(ReleaseNotesDetector.SectionHasNotes(section));

    [Fact]
    public void Found_WhenTheBodyHasNotes_IgnoresTheSections() =>
        Assert.True(ReleaseNotesDetector.Found(Fixture("container-images-2389"), ["## 1.2.3\n\nprose"]));

    [Fact]
    public void Found_WhenTheBodyHasNoNotes_NeedsAFetchedSectionWithNotesForEveryUpdate()
    {
        const string Noted = "## 1.2.3\n\n- the crash";
        var body = Doc([Row("a", "o/a"), Row("b", "o/b")], Block(Versioned + TitleOnly, "o/a (a)"));

        Assert.True(ReleaseNotesDetector.Found(body, [Noted, "## 2.0.1\n\n* another"]));
        Assert.False(ReleaseNotesDetector.Found(body, [Noted]));
        Assert.False(ReleaseNotesDetector.Found(body, [Noted, "## 2.0.1\n\nprose"]));
        Assert.False(ReleaseNotesDetector.Found(body, [Noted, Noted, Noted]));
    }

    [Fact]
    public void Found_WhenTwoUpdatesShareASourceAndANewVersion_NeedsOneFetchedSection()
    {
        var body = Doc([Row("a", "o/r"), Row("b", "o/r")], Block(Versioned + TitleOnly));

        Assert.True(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash"]));
    }

    [Fact]
    public void Found_WhenAnUpdateCannotBeFetched_IsFalse()
    {
        var body = Doc([Row("a", "o/a"), Row("b", null)], Block(Versioned + TitleOnly, "o/a (a)"));

        Assert.False(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash"]));
        Assert.False(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash", "## 1.2.3\n\n- the crash"]));
    }

    [Fact]
    public void Found_WhenAnUpdateSourceIsAPathInsideARepo_NeedsTheNotesInTheBody()
    {
        var path = Row("a", "o/a/tree/HEAD/packages/a");

        Assert.False(ReleaseNotesDetector.Found(Doc([path], Block(Versioned + TitleOnly, "o/a (a)")), ["## 1.2.3\n\n- the crash"]));
        Assert.True(ReleaseNotesDetector.Found(Doc([path], Block(Versioned + "- fixed a crash", "o/a (a)")), null));
    }

    [Fact]
    public void Found_WhenAnUpdateHasARangeTheFetcherCannotRead_IsFalse()
    {
        var body = Doc([Row("a", "o/a"), Row("b", "o/b", "~> 1.2")], Block(Versioned + TitleOnly, "o/a (a)"));

        Assert.False(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash"]));
    }

    [Fact]
    public void Found_WhenThereAreMoreUpdatesThanTheFetcherTakes_IsFalse()
    {
        var sources = Enumerable.Range(1, ChangelogFetcher.MaxDependencies + 1).Select(i => $"o/r{i}").ToArray();
        var body = Doc([.. sources.Select(s => Row(s, s))], Block(Versioned + TitleOnly));
        var fetched = Enumerable.Repeat("## 1.2.3\n\n- the crash", ChangelogFetcher.MaxDependencies).ToList();

        Assert.False(ReleaseNotesDetector.Found(body, fetched));
        Assert.False(ReleaseNotesDetector.Found(body, [.. fetched, .. fetched[..1]]));
    }

    [Fact]
    public void Body_WithAsManyUpdatesAsTheTableParserReads_HasNoNotes()
    {
        Assert.True(ReleaseNotesDetector.BodyHasNotes(SourceDoc(RenovateBodyTable.MaxRows - 1, "- fixed a crash")));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(SourceDoc(RenovateBodyTable.MaxRows, "- fixed a crash")));
    }

    [Fact]
    public void Found_WithAsManyUpdatesAsTheTableParserReads_IsFalse()
    {
        var section = new[] { "## 1.2.3\n\n- the crash" };

        Assert.True(ReleaseNotesDetector.Found(SameSourceDoc(RenovateBodyTable.MaxRows - 1), section));
        Assert.False(ReleaseNotesDetector.Found(SameSourceDoc(RenovateBodyTable.MaxRows), section));
    }

    [Fact]
    public void Found_WhenThereAreAsManyUpdatesAsTheFetcherTakes_NeedsASectionForEach()
    {
        var sources = Enumerable.Range(1, ChangelogFetcher.MaxDependencies).Select(i => $"o/r{i}").ToArray();
        var body = Doc([.. sources.Select(s => Row(s, s))], Block(Versioned + TitleOnly));

        Assert.True(ReleaseNotesDetector.Found(body, [.. Enumerable.Repeat("## 1.2.3\n\n- the crash", ChangelogFetcher.MaxDependencies)]));
    }

    [Fact]
    public void Found_WithoutAnUpdateTable_IsFalse()
    {
        Assert.False(ReleaseNotesDetector.Found("no table here", ["## 1.2.3\n\n- the crash"]));
        Assert.False(ReleaseNotesDetector.Found("no table here", []));
        Assert.False(ReleaseNotesDetector.Found(null, []));
    }

    [Fact]
    public void Found_WithNeitherNotesNorSections_IsFalse()
    {
        Assert.False(ReleaseNotesDetector.Found(Fixture("Chromance-22"), []));
        Assert.False(ReleaseNotesDetector.Found(Fixture("Chromance-22"), null));
        Assert.False(ReleaseNotesDetector.Found(null, ["## 1.2.3\n\n- the crash"]));
        Assert.False(ReleaseNotesDetector.Found(null, null));
    }
}
