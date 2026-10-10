using AgentPlatform.Host;

var builder = WebApplication.CreateBuilder(args);

var role = AgentPlatformRoles.Resolve(builder.Configuration);

builder.AddServiceDefaults();

if (role == AgentPlatformRole.Worker)
{
    builder.Services.AddAgentPlatformWorker(builder.Configuration);
}

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

public partial class Program;
