using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class AgentRunOverridesTests
{
    [Fact]
    public void Validate_AcceptsOverridesThatSetNothing()
    {
        new AgentRunOverrides().Validate();
    }

    [Fact]
    public void Validate_AcceptsEveryValueSetToAUsableOne()
    {
        new AgentRunOverrides("opus", 90, 7.5m, new Dictionary<string, string> { ["contents"] = "read" }).Validate();
    }

    [Theory]
    [InlineData("contents")]
    [InlineData("pull_requests")]
    [InlineData("checks")]
    [InlineData("statuses")]
    [InlineData("issues")]
    [InlineData("actions")]
    public void Validate_AcceptsEachPermissionOfTheDefaultReadSet(string name)
    {
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { [name] = "read" }).Validate();
    }

    [Theory]
    [InlineData("ghcr.io/octo/exe:25988414")]
    [InlineData("ghcr.io/octo/nested/exe:v1.2_x-y")]
    [InlineData("registry.example.com:5000/octo/exe:1")]
    [InlineData("ghcr.io/octo/exe@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Validate_AcceptsAnImageVolumeThatNamesATaggedOrPinnedImage(string reference)
    {
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", reference), new AgentImageVolume("tools-2", reference)]).Validate();
    }

    public static TheoryData<AgentRunOverrides> Invalid() => new()
    {
        new AgentRunOverrides(ImageVolumes: []),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe:1"), new AgentImageVolume("exe", "ghcr.io/octo/exe:2")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("", "ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("Exe", "ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("a_b", "ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("-exe", "ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume(new string('a', 21), "ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe:")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "localhost/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe:1 --privileged")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe:1\n")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/Octo/exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/../exe:1")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "ghcr.io/octo/exe@sha256:abc")]),
        new AgentRunOverrides(ImageVolumes: [new AgentImageVolume("exe", "https://ghcr.io/octo/exe:1")]),
        new AgentRunOverrides(Model: ""),
        new AgentRunOverrides(Model: "  "),
        new AgentRunOverrides(MaxTurns: 0),
        new AgentRunOverrides(MaxTurns: -1),
        new AgentRunOverrides(MaxBudgetUsd: 0m),
        new AgentRunOverrides(MaxBudgetUsd: -0.5m),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string>()),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { ["contents"] = "write" }),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { [""] = "read" }),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { ["administration"] = "read" }),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { ["contents"] = "read", ["workflows"] = "read" }),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { ["Contents"] = "read" }),
        new AgentRunOverrides(TokenPermissions: new Dictionary<string, string> { ["PULL_REQUESTS"] = "read" }),
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Validate_RejectsAValueNoRunCouldUse(AgentRunOverrides overrides)
    {
        Assert.Throws<AgentConfigurationException>(overrides.Validate);
    }
}
