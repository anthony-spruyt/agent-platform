using System.Text.RegularExpressions;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPinTests
{
    [Fact]
    public void TheVersion_IsReadFromItsLine_WhateverElseTheFileHolds()
    {
        const string pin = "# renovate: depName=octo/livemap datasource=github-release-attachments digest=sha256\nLIVEMAP_VERSION: \"v2.3.4\"\nLIVEMAP_SHA256: \"abc\"\n";

        Assert.Equal("v2.3.4", LivemapPin.ReadVersion(pin));
    }

    [Theory]
    [InlineData("LIVEMAP_VERSION: \"v2.3.4\"\r\nLIVEMAP_SHA256: \"abc\"\r\n")]
    [InlineData("  LIVEMAP_VERSION:   \"v2.3.4\"   \n")]
    [InlineData("LIVEMAP_VERSION: \"v2.3.4\"")]
    public void TheVersion_SurvivesLineEndingsAndSpacing(string pin)
    {
        Assert.Equal("v2.3.4", LivemapPin.ReadVersion(pin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("LIVEMAP_SHA256: \"abc\"\n")]
    [InlineData("# LIVEMAP_VERSION: \"v2.3.4\"\n")]
    [InlineData("OTHER_LIVEMAP_VERSION: \"v2.3.4\"\n")]
    [InlineData("LIVEMAP_VERSION: v2.3.4\n")]
    [InlineData("LIVEMAP_VERSION: \"2.3.4\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v2.3\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v2.3.4-rc1\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v2.3.4\" # bumped\n")]
    [InlineData("LIVEMAP_VERSION: \"v2.3.4/../x\"\n")]
    [InlineData("LIVEMAP_VERSION: \"\"\n")]
    public void ALineThatIsNotAPlainReleaseTag_GivesNoVersion(string pin)
    {
        Assert.Null(LivemapPin.ReadVersion(pin));
    }

    [Fact]
    public void AnotherKeyThatEndsTheSame_DoesNotMakeTheVersionAmbiguous()
    {
        Assert.Equal("v2.3.4", LivemapPin.ReadVersion("OTHER_LIVEMAP_VERSION: \"v9.9.9\"\nLIVEMAP_VERSION: \"v2.3.4\"\n"));
    }

    [Fact]
    public void TwoVersionLines_AreAmbiguous_AndGiveNoVersion()
    {
        Assert.Null(LivemapPin.ReadVersion("LIVEMAP_VERSION: \"v2.3.4\"\nLIVEMAP_VERSION: \"v2.3.5\"\n"));
    }

    private const string OldSha = "36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0";

    private const string NewSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string _pin = $"# renovate: depName=anthony-spruyt/enshrouded-livemap datasource=github-release-attachments digest=sha256\nLIVEMAP_VERSION: \"v1.9.1\"\nLIVEMAP_SHA256: \"{OldSha}\"\n";

    // The first match string of the shared annotated manager that reads the pin file (repo-operator, custom-managers/annotated.json5).
    private static readonly Regex _renovateManager = new(
        "#\\s*renovate:\\s*depName=(?<depName>\\S+)\\s+datasource=github-release-attachments\\s+digest=sha256\\n\\s*\\w+:[ \\t]+\"(?<currentValue>[^\"\\s]+)\"\\n\\s*\\w+:[ \\t]+\"(?<currentDigest>[a-f0-9]{64})\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void Writing_SwapsTheTwoValues_AndKeepsEveryOtherByte()
    {
        var written = LivemapPin.Write(_pin, "v1.10.0", NewSha);

        Assert.Equal(_pin.Replace("v1.9.1", "v1.10.0", StringComparison.Ordinal).Replace(OldSha, NewSha, StringComparison.Ordinal), written);
    }

    [Fact]
    public void WhatIsWritten_StillMatchesTheRenovateManager_WithTheNewValues()
    {
        var written = LivemapPin.Write(_pin, "v1.10.0", NewSha);

        var match = _renovateManager.Match(written!);
        Assert.True(match.Success);
        Assert.Equal("anthony-spruyt/enshrouded-livemap", match.Groups["depName"].Value);
        Assert.Equal("v1.10.0", match.Groups["currentValue"].Value);
        Assert.Equal(NewSha, match.Groups["currentDigest"].Value);
        Assert.Equal("v1.10.0", LivemapPin.ReadVersion(written!));
    }

    [Fact]
    public void ThePinTheTestStartsFrom_MatchesTheRenovateManager()
    {
        Assert.Matches(_renovateManager, _pin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("LIVEMAP_VERSION: \"v1.9.1\"\n")]
    [InlineData("LIVEMAP_SHA256: \"36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v1.9.1\"\nLIVEMAP_VERSION: \"v1.9.0\"\nLIVEMAP_SHA256: \"36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v1.9.1\"\nLIVEMAP_SHA256: \"36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0\"\nLIVEMAP_SHA256: \"36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0\"\n")]
    [InlineData("LIVEMAP_VERSION: v1.9.1\nLIVEMAP_SHA256: \"36aa06d0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293acfb0\"\n")]
    [InlineData("LIVEMAP_VERSION: \"v1.9.1\"\nLIVEMAP_SHA256: \"abc\"\n")]
    public void APinThatIsNotInThePinnedFormat_IsNotWritten(string pin)
    {
        Assert.Null(LivemapPin.Write(pin, "v1.10.0", NewSha));
    }

    [Theory]
    [InlineData("1.10.0", NewSha)]
    [InlineData("v1.10", NewSha)]
    [InlineData("v1.10.0-rc1", NewSha)]
    [InlineData("v1.10.0", "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("v1.10.0", "0123456789abcdef")]
    [InlineData("v1.10.0\"\nX: \"y", NewSha)]
    public void ValuesThatAreNotATagAndADigest_AreNotWritten(string tag, string sha)
    {
        Assert.Null(LivemapPin.Write(_pin, tag, sha));
    }

    [Theory]
    [InlineData("v1.10.0", "v1.9.1", true)]
    [InlineData("v2.0.0", "v1.99.99", true)]
    [InlineData("v1.9.2", "v1.9.1", true)]
    [InlineData("v1.9.1", "v1.9.1", false)]
    [InlineData("v1.9.1", "v1.10.0", false)]
    [InlineData("v1.9.9", "v2.0.0", false)]
    public void ATagIsAhead_OnlyWhenItsNumbersAreHigher(string tag, string pinned, bool expected)
    {
        Assert.Equal(expected, LivemapPin.IsAhead(tag, pinned));
    }
}
