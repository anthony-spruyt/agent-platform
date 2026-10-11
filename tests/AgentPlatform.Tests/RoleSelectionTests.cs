using AgentPlatform.Host;
using Microsoft.Extensions.Configuration;

namespace AgentPlatform.Tests;

public class RoleSelectionTests
{
    private static IConfiguration ConfigWith(string? role) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(role is null
                ? []
                : new Dictionary<string, string?> { ["AgentPlatform:Role"] = role })
            .Build();

    [Theory]
    [InlineData("Web", AgentPlatformRole.Web)]
    [InlineData("Worker", AgentPlatformRole.Worker)]
    [InlineData("Migrate", AgentPlatformRole.Migrate)]
    [InlineData("migrate", AgentPlatformRole.Migrate)]
    [InlineData("web", AgentPlatformRole.Web)]
    [InlineData("WORKER", AgentPlatformRole.Worker)]
    public void Resolve_ReturnsConfiguredRole(string value, AgentPlatformRole expected) =>
        Assert.Equal(expected, AgentPlatformRoles.Resolve(ConfigWith(value)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bogus")]
    [InlineData("1")]
    public void Resolve_RejectsMissingOrUnknownRole(string? value) =>
        Assert.Throws<InvalidOperationException>(() => AgentPlatformRoles.Resolve(ConfigWith(value)));
}
