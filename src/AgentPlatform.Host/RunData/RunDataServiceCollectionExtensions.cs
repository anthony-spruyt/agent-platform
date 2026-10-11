using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Host.RunData;

public static class RunDataServiceCollectionExtensions
{
    public const string ConnectionName = "rundata";

    public static IServiceCollection AddRunData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionName} is required.");
        }

        services.AddDbContextFactory<RunDataDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<RunDataStore>();
        return services;
    }
}
