using AgentPlatform.Host;
using AgentPlatform.Host.Gateway;

var builder = WebApplication.CreateBuilder(args);

var role = AgentPlatformRoles.Resolve(builder.Configuration);

builder.AddServiceDefaults();

if (role == AgentPlatformRole.Worker)
{
    builder.Services.AddAgentPlatformWorker(builder.Configuration);
}
else
{
    builder.Services.AddAgentGateway(builder.Configuration);
}

var app = builder.Build();

app.MapDefaultEndpoints();

if (role == AgentPlatformRole.Web)
{
    app.UseAgentGateway();
}

app.Run();

public partial class Program;
