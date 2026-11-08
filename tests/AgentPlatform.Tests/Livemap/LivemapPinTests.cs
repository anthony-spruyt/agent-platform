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
}
