using AgentPlatform.Host.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.Gateway;

public class GatewayConfigurationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static void Register(Dictionary<string, string?> settings)
    {
        settings["AgentGateway:SessionTokenKey"] = Key;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        new ServiceCollection().AddAgentGateway(configuration);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public void AnUnusablePort_FailsAtStartup(string port)
    {
        Assert.Throws<InvalidOperationException>(() => Register(new() { ["AgentGateway:Port"] = port }));
    }

    [Fact]
    public void ThePortOfTheWebhookListener_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Register(new() { ["AgentGateway:Port"] = "8080" }));
    }

    [Fact]
    public void ACustomWebhookPortEqualToTheGatewayPort_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Register(new()
        {
            ["AgentGateway:Port"] = "9000",
            ["AgentGateway:WebhookPort"] = "9000",
        }));
    }

    [Fact]
    public void TheDefaultPorts_Start()
    {
        Register(new());
    }

    [Fact]
    public void DistinctCustomPorts_Start()
    {
        Register(new()
        {
            ["AgentGateway:Port"] = "9000",
            ["AgentGateway:WebhookPort"] = "9001",
        });
    }
}
