using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class ChangelogSectionTextTests
{
    private const string KeepAChangelog = """
        # Changelog

        ## [Unreleased]

        - nothing yet

        ## [1.3.0] - 2026-02-01

        ### Added

        - a thing

        ## [1.2.3] - 2026-01-01

        ### Fixed

        - the crash

        ## [1.2.2] - 2025-12-01

        - old
        """;

    [Fact]
    public void Extract_ReturnsTheHeadingAndEverythingUpToTheNextHeadingOfTheSameLevel()
    {
        var section = ChangelogSectionText.Extract(KeepAChangelog, "1.2.3", 10_000);

        Assert.Equal("## [1.2.3] - 2026-01-01\n\n### Fixed\n\n- the crash", section);
    }

    [Fact]
    public void Extract_OfTheLastSection_RunsToTheEndOfTheFile()
    {
        Assert.Equal("## [1.2.2] - 2025-12-01\n\n- old", ChangelogSectionText.Extract(KeepAChangelog, "1.2.2", 10_000));
    }

    [Theory]
    [InlineData("# v1.2.3\n- a\n# v1.2.2\n- b", "# v1.2.3\n- a")]
    [InlineData("## 1.2.3 (2026-01-01)\n- a\n## 1.2.2\n- b", "## 1.2.3 (2026-01-01)\n- a")]
    [InlineData("### v1.2.3\n- a\n#### Fixed\n- sub\n### v1.2.2\n- b", "### v1.2.3\n- a\n#### Fixed\n- sub")]
    [InlineData("## Version 1.2.3\n- a\n# Top\n- b", "## Version 1.2.3\n- a")]
    public void Extract_FindsTheHeadingStylesChangelogsUse(string changelog, string expected) =>
        Assert.Equal(expected, ChangelogSectionText.Extract(changelog, "1.2.3", 10_000));

    [Fact]
    public void Extract_IgnoresALeadingVOnTheVersionWanted() =>
        Assert.Equal("## 1.2.3\n- a", ChangelogSectionText.Extract("## 1.2.3\n- a\n## 1.2.2\n- b", "v1.2.3", 10_000));

    [Theory]
    [InlineData("## 1.2.30\n- a")]
    [InlineData("## 11.2.3\n- a")]
    [InlineData("## 1.2.3.1\n- a")]
    [InlineData("## 1.2.3-rc.1\n- a")]
    [InlineData("## 21.2.3\n- a")]
    [InlineData("1.2.3\n- not a heading")]
    public void Extract_DoesNotMatchAnotherVersionOrANonHeading(string changelog) =>
        Assert.Null(ChangelogSectionText.Extract(changelog, "1.2.3", 10_000));

    [Fact]
    public void Extract_CutsALongSectionAtTheLimit()
    {
        var changelog = "## 1.2.3\n" + new string('x', 500);

        Assert.Equal(100, ChangelogSectionText.Extract(changelog, "1.2.3", 100)!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Extract_OfNothing_ReturnsNull(string? changelog) =>
        Assert.Null(ChangelogSectionText.Extract(changelog, "1.2.3", 100));

    [Fact]
    public void Extract_HandlesWindowsLineEndings() =>
        Assert.Equal("## 1.2.3\n- a", ChangelogSectionText.Extract("## 1.2.3\r\n- a\r\n## 1.2.2\r\n- b", "1.2.3", 1000));
}
