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

    private static string Row(string dependency, string? source, string to = "1.2.3") =>
        $"| {(source is null ? dependency : $"[{dependency}](https://redirect.github.com/{source})")} | `1.2.2` → `{to}` |";

    private static string Doc(string[] rows, params string[] blocks) =>
        "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n"
        + string.Join("\n", rows)
        + "\n\n---\n\n### Release Notes\n\n"
        + string.Join("\n\n", blocks)
        + "\n\n---\n\n### Configuration\n\n- Branch creation\n  - At any time (no schedule defined)\n- Automerge\n  - At any time (no schedule defined)\n";

    private static string Body(params string[] blocks) => Doc([Row("dep", "o/r")], blocks);

    private static string Grouped(params string[] blocks) => Doc([Row("a", "o/a"), Row("b", "o/b")], blocks);

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
    [InlineData("- fixed a crash")]
    [InlineData("* fixed a crash")]
    [InlineData("+ fixed a crash")]
    [InlineData("1. fixed a crash")]
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

    [Theory]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("###")]
    public void Body_WithABlockAfterTheConfigurationSection_IgnoresIt(string level)
    {
        var late = Block(Versioned + "- fixed a crash", "o/b (b)");
        var body = Grouped(Block(Versioned + "- fixed a crash", "o/a (a)")).Replace("### Configuration", $"{level} Configuration", StringComparison.Ordinal) + "\n" + late + "\n";

        Assert.False(ReleaseNotesDetector.BodyHasNotes(body));
    }

    [Theory]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("######")]
    public void Body_WithAVersionHeadingOfAnyLevel_HasNotes(string level) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned.Replace("###", level, StringComparison.Ordinal) + "- fixed a crash"))));

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
    public void Body_WithBulletsOnlyInsideATildeFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "~~~\n- fixed a crash\n~~~"))));

    [Fact]
    public void Body_WithAVersionHeadingInsideACodeFence_KeepsTheNewestSectionOpen() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```md\n" + Older + "```\n\n- fixed a crash"))));

    [Fact]
    public void Body_WithBulletsInsideAMultiLineComment_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "<!--\n- fixed a crash\n-->"))));

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

    [Fact]
    public void Body_WhoseDetailsBlockNeverCloses_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")[..^"</details>".Length])));

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
    [InlineData("^1.2.3")]
    [InlineData("~>1.2")]
    [InlineData(">=1.2,<1.3")]
    [InlineData("1.2.3-alpine")]
    public void Body_WhereTheNewVersionIsNotAPlainVersion_HasNoNotes(string to) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Doc([Row("dep", "o/r", to)], Block(Versioned + "- fixed a crash"))));

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
    }

    [Fact]
    public void Body_WithAClosingTagInsideInlineCode_StillChecksTheLaterBlocks()
    {
        var first = Block(Versioned + "- fixed the `</details>` tag\n\n" + Older + "- older", "o/a (a)");

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
    public void Found_WhenThereAreMoreUpdatesThanTheFetcherTakes_IsFalse()
    {
        var sources = Enumerable.Range(1, ChangelogFetcher.MaxDependencies + 1).Select(i => $"o/r{i}").ToArray();
        var body = Doc([.. sources.Select(s => Row(s, s))], Block(Versioned + TitleOnly));
        var fetched = Enumerable.Repeat("## 1.2.3\n\n- the crash", ChangelogFetcher.MaxDependencies).ToList();

        Assert.False(ReleaseNotesDetector.Found(body, fetched));
        Assert.False(ReleaseNotesDetector.Found(body, [.. fetched, .. fetched[..1]]));
    }

    [Fact]
    public void Found_WithoutAnUpdateTable_IsFalse() =>
        Assert.False(ReleaseNotesDetector.Found("no table here", ["## 1.2.3\n\n- the crash"]));

    [Fact]
    public void Found_WithNeitherNotesNorSections_IsFalse()
    {
        Assert.False(ReleaseNotesDetector.Found(Fixture("Chromance-22"), []));
        Assert.False(ReleaseNotesDetector.Found(Fixture("Chromance-22"), null));
        Assert.False(ReleaseNotesDetector.Found(null, ["## 1.2.3\n\n- the crash"]));
        Assert.False(ReleaseNotesDetector.Found(null, null));
    }
}
