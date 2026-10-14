using System.Diagnostics.Metrics;

namespace AgentPlatform.Host.RenovateTriage;

public sealed class TriageGauges : IDisposable
{
    public const string MeterName = "AgentPlatform.Triage";

    private int _pending;
    private int _cooldowns;

    public TriageGauges(TriagePendingStore pending, ClaudeBudgetStore budget)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(budget);
        Pending = pending;
        Budget = budget;
        Meter = new Meter(MeterName);
        Meter.CreateObservableGauge("triage_pending_total", () => Volatile.Read(ref _pending), description: "Renovate PRs queued for the Claude rung.");
        Meter.CreateObservableGauge("claude_cooldown_active", () => Volatile.Read(ref _cooldowns), description: "Claude cooldowns currently in force.");
    }

    internal Meter Meter { get; }

    private TriagePendingStore Pending { get; }

    private ClaudeBudgetStore Budget { get; }

    // Gauges only read the cached values: the OpenTelemetry reader's callback is synchronous and must not wait on the database.
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var pending = await Pending.CountOpenAsync(cancellationToken).ConfigureAwait(false);
        var cooldowns = await Budget.CountActiveCooldownsAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _pending, pending);
        Volatile.Write(ref _cooldowns, cooldowns);
    }

    public void Dispose() => Meter.Dispose();
}
