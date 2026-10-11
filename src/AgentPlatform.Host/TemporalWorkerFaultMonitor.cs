using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Host;

// Must be registered after the Temporal workers: it reads each worker's ExecuteTask, which exists once the worker has started.
// Resolves hosted services lazily; injecting IEnumerable<IHostedService> would be a circular dependency.
internal sealed partial class TemporalWorkerFaultMonitor(
    IServiceProvider services,
    IExitCodeSetter exitCode,
    ILogger<TemporalWorkerFaultMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerTasks = services.GetServices<IHostedService>()
            .OfType<TemporalWorkerService>()
            .Select(worker => worker.ExecuteTask ?? throw new InvalidOperationException("A Temporal worker has not started."))
            .ToList();

        try
        {
            var first = await Task.WhenAny(workerTasks).WaitAsync(stoppingToken).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && !workerTasks.Any(t => t.IsFaulted))
        {
        }
        catch (Exception ex)
        {
            LogWorkerFaulted(logger, ex);
            exitCode.Apply(1);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Temporal worker faulted; exiting with a non-zero exit code.")]
    private static partial void LogWorkerFaulted(ILogger logger, Exception exception);
}
