using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.Support;

internal sealed class InMemoryRunData
{
    private InMemoryRunData(IDbContextFactory<RunDataDbContext> factory)
    {
        Factory = factory;
        Store = new RunDataStore(factory);
    }

    public IDbContextFactory<RunDataDbContext> Factory { get; }

    public RunDataStore Store { get; }

    public RunDataActivities Activities => new(Store);

    public static InMemoryRunData Create()
    {
        var name = Guid.NewGuid().ToString();
        var provider = new ServiceCollection()
            .AddDbContextFactory<RunDataDbContext>(options => options.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        return new InMemoryRunData(provider.GetRequiredService<IDbContextFactory<RunDataDbContext>>());
    }
}
