using AgentPlatform.Host.RenovateTriage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Sweeper;

public class SweeperScheduleServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SweeperScheduleService Service(ITemporalClient client, TimeSpan? interval = null) =>
        new(
            client,
            Options.Create(new RenovateTriageOptions { Sweeper = new SweeperOptions { Interval = interval ?? TimeSpan.FromMinutes(30) } }),
            NullLogger<SweeperScheduleService>.Instance,
            TimeSpan.FromMilliseconds(10));

    private static async Task RunOnceAsync(SweeperScheduleService service)
    {
        await service.StartAsync(Ct);
        await service.ExecuteTask!;
        await service.StopAsync(Ct);
    }

    private static async Task<TimeSpan> IntervalAsync(ITemporalClient client)
    {
        var description = await client.GetScheduleHandle(SweeperScheduleService.ScheduleId).DescribeAsync();
        return Assert.Single(description.Schedule.Spec.Intervals).Every;
    }

    [Fact]
    public async Task StartingTheService_CreatesTheScheduleWithTheConfiguredInterval_AndSkipsOverlappingRuns()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        await RunOnceAsync(Service(env.Client, TimeSpan.FromMinutes(10)));

        var description = await env.Client.GetScheduleHandle(SweeperScheduleService.ScheduleId).DescribeAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(description.Schedule.Spec.Intervals).Every);
        Assert.Equal(Temporalio.Api.Enums.V1.ScheduleOverlapPolicy.Skip, description.Schedule.Policy.Overlap);
    }

    [Fact]
    public async Task StartingTheServiceAgain_UpdatesTheExistingSchedule_InsteadOfCreatingASecond()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        await RunOnceAsync(Service(env.Client, TimeSpan.FromMinutes(30)));

        await RunOnceAsync(Service(env.Client, TimeSpan.FromMinutes(5)));

        Assert.Equal(TimeSpan.FromMinutes(5), await IntervalAsync(env.Client));
        var schedules = new List<string>();
        await foreach (var schedule in env.Client.ListSchedulesAsync())
        {
            schedules.Add(schedule.Id);
        }

        Assert.Equal([SweeperScheduleService.ScheduleId], schedules);
    }
}
