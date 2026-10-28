using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
            .AddDbContextFactory<RunDataDbContext>(options => options.UseInMemoryDatabase(name).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).AddInterceptors(interceptor))
            .BuildServiceProvider();
        return new InMemoryRunData(provider.GetRequiredService<IDbContextFactory<RunDataDbContext>>(), interceptor);
    }

    public static (ClaudeBudgetStore Store, InMemoryRunData Data) CreateWithBudget(DateTimeOffset now, int perHour, int perDay, int perPr)
    {
        var data = Create();
        var options = Options.Create(new RenovateTriageOptions { Claude = new ClaudeBudgetOptions { MaxPerHour = perHour, MaxPerDay = perDay, MaxPerPrPerDay = perPr } });
        return (new ClaudeBudgetStore(data.Factory, options, new FixedTimeProvider(now)), data);
    }

    public async Task SeedRunAsync(string repo, int prNumber, DateTimeOffset at, CancellationToken cancellationToken, ClaudeRunOutcome? outcome = ClaudeRunOutcome.Succeeded)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        db.ClaudeRuns.Add(new ClaudeRun
        {
            Id = Guid.NewGuid(),
            Repo = repo,
            PrNumber = prNumber,
            HeadSha = FakeGitHubApi.Sha,
            WorkflowId = "wf",
            Attempt = 1,
            ReservedAt = at,
            PodCreatedAt = at,
            FinishedAt = at,
            Outcome = outcome,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SeedCooldownAsync(string scope, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        db.ClaudeCooldowns.Add(new ClaudeCooldown { Scope = scope, Until = until, Cause = "usage-limit" });
        await db.SaveChangesAsync(cancellationToken);
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
