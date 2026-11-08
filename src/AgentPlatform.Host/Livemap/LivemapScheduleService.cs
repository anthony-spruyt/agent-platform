using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace AgentPlatform.Host.Livemap;

public sealed partial class LivemapScheduleService(ITemporalClient client, IOptions<LivemapOptions> options, ILogger<LivemapScheduleService> logger, TimeSpan retryDelay) : BackgroundService
{
    public const string ScheduleId = "livemap-poll";

    private static readonly TimeSpan _defaultRetryDelay = TimeSpan.FromSeconds(30);

    public LivemapScheduleService(ITemporalClient client, IOptions<LivemapOptions> options, ILogger<LivemapScheduleService> logger)
        : this(client, options, logger, _defaultRetryDelay)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!await TryEnsureAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryEnsureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await (options.Value.Enabled ? CreateOrUpdateAsync(cancellationToken) : DeleteAsync(cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogScheduleFailed(logger, ex);
            return false;
        }
    }

    private async Task CreateOrUpdateAsync(CancellationToken cancellationToken)
    {
        var schedule = new Schedule(
            ScheduleActionStartWorkflow.Create(
                (LivemapPollWorkflow workflow) => workflow.RunAsync(),
                new WorkflowOptions(LivemapPollWorkflow.WorkflowId, TaskQueues.AgentPlatform)),
            new ScheduleSpec { Intervals = [new ScheduleIntervalSpec(options.Value.PollInterval)] })
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

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.GetScheduleHandle(ScheduleId).DeleteAsync(new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating, updating or removing the Livemap poll schedule failed; retrying.")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception);
}
