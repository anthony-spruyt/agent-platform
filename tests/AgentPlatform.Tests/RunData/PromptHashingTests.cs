using AgentPlatform.Host.RunData;

namespace AgentPlatform.Tests.RunData;

public class PromptHashingTests
{
    [Fact]
    public void Compute_IsTheLowercaseHexSha256OfTheUtf8Body() =>
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", PromptHashing.Compute("hello"));

    [Fact]
    public void Compute_DiffersWhenTheBodyDiffers() =>
        Assert.NotEqual(PromptHashing.Compute("a"), PromptHashing.Compute("a "));
}
