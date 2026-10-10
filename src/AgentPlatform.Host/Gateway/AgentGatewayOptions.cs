namespace AgentPlatform.Host.Gateway;

public sealed class AgentGatewayOptions
{
    public const string SectionName = "AgentGateway";

    public string SessionTokenKey { get; set; } = string.Empty;

    public int Port { get; set; } = 8081;

    public int WebhookPort { get; set; } = 8080;
}
