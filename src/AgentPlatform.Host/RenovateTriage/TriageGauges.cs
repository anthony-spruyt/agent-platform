using System.Diagnostics.Metrics;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageGauges : IDisposable
{
    public const string MeterName = "AgentPlatform.Triage";

    private int _pending;
    private int _cooldowns;
    private double _sweeperLastSuccess;

    public TriageGauges(TriagePendingStore pending, ClaudeBudgetStore budget, SweeperStore sweeper)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sweeper);
        Pending = pending;
        Budget = budget;
        Sweeper = sweeper;
        Meter = new Meter(MeterName);
        Meter.CreateObservableGauge("triage_pending_total", () => Volatile.Read(ref _pending), description: "Renovate PRs queued for the Claude rung; held (blocked) ones are not counted.");
        Meter.CreateObservableGauge("claude_cooldown_active", () => Volatile.Read(ref _cooldowns), description: "Claude cooldowns currently in force.");
        Meter.CreateObservableGauge("sweeper_last_success_timestamp", () => Volatile.Read(ref _sweeperLastSuccess), description: "Unix time of the last triage sweeper run in which every step succeeded; 0 before the first.");
    }

    internal Meter Meter { get; }

    private TriagePendingStore Pending { get; }

    private ClaudeBudgetStore Budget { get; }

    private SweeperStore Sweeper { get; }

    // Gauges only read the cached values: the OpenTelemetry reader's callback is synchronous and must not wait on the database.
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var pending = await Pending.CountOpenAsync(cancellationToken).ConfigureAwait(false);
        var cooldowns = await Budget.CountActiveCooldownsAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _pending, pending);
        var lastSuccess = (await Sweeper.GetAsync(cancellationToken).ConfigureAwait(false)).LastSuccessAt;
        Volatile.Write(ref _cooldowns, cooldowns);
        Volatile.Write(ref _sweeperLastSuccess, lastSuccess?.ToUnixTimeSeconds() ?? 0);
    }

    public void Dispose() => Meter.Dispose();
}
