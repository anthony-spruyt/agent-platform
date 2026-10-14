using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.RenovateTriage;

public sealed partial class SweeperScheduleService(ITemporalClient client, IOptions<RenovateTriageOptions> options, ILogger<SweeperScheduleService> logger, TimeSpan retryDelay) : BackgroundService
{
    public const string ScheduleId = "renovate-triage-sweeper";

    private static readonly TimeSpan _defaultRetryDelay = TimeSpan.FromSeconds(30);

    public SweeperScheduleService(ITemporalClient client, IOptions<RenovateTriageOptions> options, ILogger<SweeperScheduleService> logger)
        : this(client, options, logger, _defaultRetryDelay)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureScheduleAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogScheduleFailed(logger, ex);
            }

            try
            {
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task EnsureScheduleAsync(CancellationToken cancellationToken)
    {
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (RenovateTriageSweeperWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(RenovateTriageSweeperWorkflow.WorkflowId, TaskQueues.AgentPlatform)),
            new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(options.Value.Sweeper.Interval)] })
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
        };

        try
        {
            await client.CreateScheduleAsync(ScheduleId, schedule, new ScheduleOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
        }
        catch (ScheduleAlreadyRunningException)
        {
            await client.GetScheduleHandle(ScheduleId)
                .UpdateAsync(_ => new ScheduleUpdate(schedule), new RpcOptions { CancellationToken = cancellationToken })
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating or updating the triage sweeper schedule failed; retrying.")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception);
}
