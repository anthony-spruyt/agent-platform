using AgentPlatform.Host;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.RunData;

var builder = WebApplication.CreateBuilder(args);

var role = AgentPlatformRoles.Resolve(builder.Configuration);

builder.AddServiceDefaults();

switch (role)
{
    case AgentPlatformRole.Worker:
        builder.Services.AddAgentPlatformWorker(builder.Configuration);
        break;
    case AgentPlatformRole.Migrate:
        builder.Services.AddRunData(builder.Configuration);
        builder.Services.AddSingleton<RunDataMigrator>();
        break;
    default:
        builder.Services.AddAgentGateway(builder.Configuration);
        break;
}

var app = builder.Build();

if (role == AgentPlatformRole.Migrate)
{
    return await app.Services.GetRequiredService<RunDataMigrator>().RunAsync(CancellationToken.None);
}

app.MapDefaultEndpoints();

if (role == AgentPlatformRole.Web)
{
    app.UseAgentGateway();
}

app.Run();
return Environment.ExitCode;

public partial class Program;
