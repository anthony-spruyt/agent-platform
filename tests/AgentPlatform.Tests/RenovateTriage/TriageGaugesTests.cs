using System.Diagnostics.Metrics;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.RenovateTriage;

public class TriageGaugesTests
{
    private const string Repo = "owner/repo";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TriageGauges Gauges, InMemoryRunData Data, FlakyFactory Factory) Create()
    {
        var data = InMemoryRunData.Create();
        var factory = new FlakyFactory(data.Factory);
        var pending = new TriagePendingStore(factory, TimeProvider.System);
        var budget = new ClaudeBudgetStore(factory, Options.Create(new RenovateTriageOptions()), TimeProvider.System);
        return (new TriageGauges(pending, budget), data, factory);
    }

    private static Dictionary<string, int> Read(TriageGauges gauges)
    {
        var values = new Dictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, gauges.Meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    [Fact]
    public async Task TheGauges_ReportTheOpenPendingRowsAndTheActiveCooldowns_AfterARefresh()
    {
        var (gauges, data, _) = Create();
        using var _ = gauges;
        await data.Pending.UpsertAsync(Repo, 1, Sha, "breaking", Ct);
        await data.Pending.UpsertAsync(Repo, 2, Sha, "breaking", Ct);
        await data.Pending.UpsertAsync(Repo, 3, Sha, "breaking", Ct);
        await data.Pending.SetStatusAsync(Repo, 3, Sha, TriagePendingStatus.Settled, Ct);
        await using (var db = await data.Factory.CreateDbContextAsync(Ct))
        {
            db.ClaudeCooldowns.Add(new ClaudeCooldown { Scope = ClaudeCooldown.Global, Until = DateTimeOffset.UtcNow.AddHours(1), Cause = "usage-limit" });
            db.ClaudeCooldowns.Add(new ClaudeCooldown { Scope = "owner/old", Until = DateTimeOffset.UtcNow.AddHours(-1), Cause = "usage-limit" });
            await db.SaveChangesAsync(Ct);
        }

        await gauges.RefreshAsync(Ct);

        var values = Read(gauges);
        Assert.Equal(2, values["triage_pending_total"]);
        Assert.Equal(1, values["claude_cooldown_active"]);
    }

    [Fact]
    public void BeforeTheFirstRefresh_TheGaugesReportZero()
    {
        var (gauges, _, _) = Create();
        using var _ = gauges;

        var values = Read(gauges);

        Assert.Equal(0, values["triage_pending_total"]);
        Assert.Equal(0, values["claude_cooldown_active"]);
    }

    [Fact]
    public async Task TheRefresher_KeepsTheGaugesCurrent_AndSurvivesAFailedRefresh()
    {
        var (gauges, data, factory) = Create();
        using var _ = gauges;
        using var refresher = new TriageGaugeRefresher(gauges, NullLogger<TriageGaugeRefresher>.Instance, TimeSpan.FromMilliseconds(20));
        factory.Down = true;
        await refresher.StartAsync(Ct);
        await Task.Delay(100, Ct);
        factory.Down = false;
        await data.Pending.UpsertAsync(Repo, 1, Sha, "breaking", Ct);

        await WaitForAsync(() => Read(gauges)["triage_pending_total"] == 1);

        await refresher.StopAsync(Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the gauge.");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class FlakyFactory(IDbContextFactory<RunDataDbContext> inner) : IDbContextFactory<RunDataDbContext>
    {
        public bool Down { get; set; }

        public RunDataDbContext CreateDbContext() =>
            Down ? throw new InvalidOperationException("the database is down") : inner.CreateDbContext();

        public Task<RunDataDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Down ? throw new InvalidOperationException("the database is down") : inner.CreateDbContextAsync(cancellationToken);
    }
}
