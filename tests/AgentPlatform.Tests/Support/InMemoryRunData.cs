using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.Support;

internal sealed class InMemoryRunData
{
    private readonly FailingSaveInterceptor _interceptor;

    private InMemoryRunData(IDbContextFactory<RunDataDbContext> factory, FailingSaveInterceptor interceptor)
    {
        Factory = factory;
        _interceptor = interceptor;
        Store = new RunDataStore(factory);
    }

    public IDbContextFactory<RunDataDbContext> Factory { get; }

    public RunDataStore Store { get; }

    public RunDataActivities Activities => new(Store);

    public static InMemoryRunData Create()
    {
        var name = Guid.NewGuid().ToString();
        var interceptor = new FailingSaveInterceptor();
        var provider = new ServiceCollection()
            .AddDbContextFactory<RunDataDbContext>(options => options.UseInMemoryDatabase(name).AddInterceptors(interceptor))
            .BuildServiceProvider();
        return new InMemoryRunData(provider.GetRequiredService<IDbContextFactory<RunDataDbContext>>(), interceptor);
    }

    public void FailSavesWith(Exception failure) => _interceptor.Failure = failure;

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? Failure { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Failure is null ? base.SavingChangesAsync(eventData, result, cancellationToken) : throw Failure;
    }
}
