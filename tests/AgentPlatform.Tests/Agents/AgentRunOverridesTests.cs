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

    public static TheoryData<AgentRunOverrides> Invalid() => new()
    {
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
