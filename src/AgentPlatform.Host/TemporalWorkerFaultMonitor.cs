using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Hosting;

namespace AgentPlatform.Host;

// Must be registered after the Temporal worker: it reads the worker's ExecuteTask, which exists once the worker has started.
// Resolves hosted services lazily; injecting IEnumerable<IHostedService> would be a circular dependency.
internal sealed partial class TemporalWorkerFaultMonitor(
    IServiceProvider services,
    IExitCodeSetter exitCode,
    ILogger<TemporalWorkerFaultMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerTask = services.GetServices<IHostedService>().OfType<TemporalWorkerService>().Single().ExecuteTask
            ?? throw new InvalidOperationException("The Temporal worker has not started.");

        try
        {
            await workerTask.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && !workerTask.IsFaulted)
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
