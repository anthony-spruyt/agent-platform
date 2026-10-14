using AgentPlatform.Host.RenovateTriage;
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
        Verdicts = new TriageVerdictStore(factory);
        Pending = new TriagePendingStore(factory, TimeProvider.System);
    }

    public IDbContextFactory<RunDataDbContext> Factory { get; }

    public RunDataStore Store { get; }

    public TriageVerdictStore Verdicts { get; }

    public TriagePendingStore Pending { get; }

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

    public void FailNextSaveWith(Exception failure) => _interceptor.FailOnce = failure;

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? Failure { get; set; }

        public Exception? FailOnce { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailOnce is { } once)
            {
                FailOnce = null;
                throw once;
            }

            return Failure is null ? base.SavingChangesAsync(eventData, result, cancellationToken) : throw Failure;
        }
    }
}
