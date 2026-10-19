using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class ReleaseNotesDetectorTests
{
    private const string Versioned = "### [`v1.2.3`](https://redirect.github.com/o/r/releases/tag/v1.2.3)\n\n[Compare Source](https://redirect.github.com/o/r/compare/v1.2.2...v1.2.3)\n\n";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", $"release-notes-{name}.txt"));

    private static string Block(string inner, string summary = "o/r (dep)") => $"<details>\n<summary>{summary}</summary>\n\n{inner}\n\n</details>";

    private static string Body(params string[] blocks) =>
        "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://redirect.github.com/o/r) | `1.2.2` → `1.2.3` |\n\n---\n\n### Release Notes\n\n"
        + string.Join("\n\n", blocks)
        + "\n\n---\n\n### Configuration\n\n- Branch creation\n  - At any time (no schedule defined)\n- Automerge\n  - At any time (no schedule defined)\n";

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Body_ThatIsEmpty_HasNoNotes(string? body) =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(body));

    [Theory]
    [InlineData("- fixed a crash")]
    [InlineData("* fixed a crash")]
    [InlineData("+ fixed a crash")]
    [InlineData("1. fixed a crash")]
    [InlineData("  - fixed a crash")]
    [InlineData("- [#12](https://github.com/o/r/pull/12) fixed a crash")]
    [InlineData("- fix: a crash by @someone in https://github.com/o/r/pull/12")]
    public void Body_WithABulletThatHasText_HasNotes(string bullet) =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + bullet))));

    [Theory]
    [InlineData("- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)")]
    [InlineData("- https://github.com/o/r/pull/12")]
    [InlineData("- <https://github.com/o/r/pull/12>")]
    [InlineData("* [#12](https://github.com/o/r/pull/12)")]
    [InlineData("- **[v1.2.3](https://github.com/o/r/releases/tag/v1.2.3)**")]
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
    public void Body_WithBulletsOnlyInTheConfigurationSection_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "Python 1.2.3"))));

    [Fact]
    public void Body_WhereTheBulletIsOnlyUnderAnOlderVersionSection_HasNoNotes()
    {
        var older = "### [`v1.2.2`](https://redirect.github.com/o/r/releases/tag/v1.2.2)\n\n- fixed a crash";
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "\n\n" + older))));
    }

    [Fact]
    public void Body_WhereTheNewestVersionSectionHasABulletAndOlderOnesDoNot_HasNotes()
    {
        var older = "### [`v1.2.2`](https://redirect.github.com/o/r/releases/tag/v1.2.2)\n\nprose";
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash\n\n" + older))));
    }

    [Fact]
    public void Body_WithSubHeadingsUnderTheVersion_ReadsTheBulletsBelowThem() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "##### Bug Fixes\n\n* fixed a crash"))));

    [Fact]
    public void Body_WithBulletsOnlyInsideACodeFence_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "```yaml\n- fixed a crash\n```"))));

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
    public void Body_WithWindowsLineEndings_HasNotes() =>
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(Block(Versioned + "- fixed a crash")).Replace("\n", "\r\n", StringComparison.Ordinal)));

    [Fact]
    public void Body_OfAGroupedPr_HasNotesOnlyWhenEveryDependencyBlockDoes()
    {
        var noted = Block(Versioned + "- fixed a crash", "o/a (a)");
        var titleOnly = Block(Versioned + "Python 1.2.3", "o/b (b)");

        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(noted, titleOnly)));
        Assert.False(ReleaseNotesDetector.BodyHasNotes(Body(titleOnly, noted)));
        Assert.True(ReleaseNotesDetector.BodyHasNotes(Body(noted, Block(Versioned + "* fixed another crash", "o/b (b)"))));
    }

    [Fact]
    public void Section_WithABullet_HasNotes() =>
        Assert.True(ReleaseNotesDetector.SectionHasNotes("## [1.2.3] - 2026-01-01\n\n### Fixed\n\n- the crash"));

    [Fact]
    public void Section_WithoutABullet_HasNoNotes() =>
        Assert.False(ReleaseNotesDetector.SectionHasNotes("## 1.2.3\n\nPython 1.2.3"));

    [Theory]
    [InlineData("## 1.2.3\n\n- [Full Changelog](https://github.com/o/r/compare/v1.2.2...v1.2.3)")]
    [InlineData("## 1.2.3\n\n* https://github.com/o/r/pull/12")]
    [InlineData("## 1.2.3\n\n```\n- the crash\n```")]
    [InlineData("")]
    [InlineData(null)]
    public void Section_WithOnlyLinksOrNoText_HasNoNotes(string? section) =>
        Assert.False(ReleaseNotesDetector.SectionHasNotes(section));

    [Fact]
    public void Found_WhenTheBodyHasNotes_IgnoresTheSections() =>
        Assert.True(ReleaseNotesDetector.Found(Fixture("container-images-2389"), ["## 1.2.3\n\nprose"]));

    [Fact]
    public void Found_WhenTheBodyHasNoNotes_NeedsEverySectionToHaveThem()
    {
        var body = Fixture("Chromance-22");

        Assert.True(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash"]));
        Assert.True(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash", "## 2.0.1\n\n* another"]));
        Assert.False(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\n- the crash", "## 2.0.1\n\nprose"]));
        Assert.False(ReleaseNotesDetector.Found(body, ["## 1.2.3\n\nprose"]));
    }

    [Fact]
    public void Found_WithNeitherNotesNorSections_IsFalse()
    {
        Assert.False(ReleaseNotesDetector.Found(Fixture("Chromance-22"), []));
        Assert.False(ReleaseNotesDetector.Found(null, null));
    }
}
