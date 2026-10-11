using AgentPlatform.Host.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Host.Gateway;

public static class AgentGatewayExtensions
{
    public static IServiceCollection AddAgentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AgentGatewayOptions.SectionName);
        var gateway = section.Get<AgentGatewayOptions>() ?? new AgentGatewayOptions();
        Validate(gateway);
        var temporal = configuration.GetSection(TemporalOptions.SectionName).Get<TemporalOptions>() ?? new TemporalOptions();

        services.Configure<AgentGatewayOptions>(section);
        services.AddSingleton(new AgentSessionTokens(gateway.SessionTokenKey));
        services.AddTemporalClient(temporal.Address, temporal.Namespace);
        services.AddSingleton<IWorkflowResultSubmitter, TemporalWorkflowResultSubmitter>();
        services.AddHttpContextAccessor();
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<SubmitResultTool>();

        return services;
    }

    private static void Validate(AgentGatewayOptions gateway)
    {
        if (gateway.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("AgentGateway:Port must be between 1 and 65535.");
        }

        if (gateway.Port == gateway.WebhookPort)
        {
            throw new InvalidOperationException("AgentGateway:Port must differ from AgentGateway:WebhookPort.");
        }
    }

    public static WebApplication UseAgentGateway(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<AgentGatewayAuthMiddleware>();
        app.MapMcp(AgentGatewayAuthMiddleware.McpPath);
        return app;
    }
}
