using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Host.RenovateTriage;

public sealed partial class TriageGaugeRefresher(TriageGauges gauges, ILogger<TriageGaugeRefresher> logger, TimeSpan interval) : BackgroundService
{
    private static readonly TimeSpan _defaultInterval = TimeSpan.FromSeconds(30);

    public TriageGaugeRefresher(TriageGauges gauges, ILogger<TriageGaugeRefresher> logger)
        : this(gauges, logger, _defaultInterval)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await gauges.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the triage gauges failed; the previous values stay.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
