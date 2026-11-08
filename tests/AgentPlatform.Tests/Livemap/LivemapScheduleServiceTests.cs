using AgentPlatform.Host.Livemap;
using AgentPlatform.Host.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Livemap;

public class LivemapScheduleServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LivemapScheduleService Service(ITemporalClient client, bool enabled = true, TimeSpan? interval = null) =>
        new(
            client,
            Options.Create(new LivemapOptions { Enabled = enabled, PollInterval = interval ?? TimeSpan.FromMinutes(15) }),
            NullLogger<LivemapScheduleService>.Instance,
            TimeSpan.FromMilliseconds(10));

    private static async Task RunOnceAsync(LivemapScheduleService service)
    {
        await service.StartAsync(Ct);
        await service.ExecuteTask!;
        await service.StopAsync(Ct);
    }

    private static async Task<List<string>> ScheduleIdsAsync(ITemporalClient client)
    {
        var ids = new List<string>();
        await foreach (var schedule in client.ListSchedulesAsync())
        {
            ids.Add(schedule.Id);
        }

        return ids;
    }

    private sealed class FailFirstCreate : IClientInterceptor
    {
        public int Attempts { get; private set; }

        public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor nextInterceptor) => new Outbound(this, nextInterceptor);

        private sealed class Outbound(FailFirstCreate owner, ClientOutboundInterceptor next) : ClientOutboundInterceptor(next)
        {
            public override Task<ScheduleHandle> CreateScheduleAsync(CreateScheduleInput input)
            {
                return ++owner.Attempts == 1 ? throw new InvalidOperationException("temporal is not up yet") : base.CreateScheduleAsync(input);
            }
        }
    }

    [Fact]
    public async Task WhenEnabled_TheScheduleStartsThePollOnTheConfiguredInterval_AndSkipsOverlappingRuns()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        await RunOnceAsync(Service(env.Client, interval: TimeSpan.FromMinutes(10)));

        var description = await env.Client.GetScheduleHandle(LivemapScheduleService.ScheduleId).DescribeAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(description.Schedule.Spec.Intervals).Every);
        Assert.Equal(ScheduleOverlapPolicy.Skip, description.Schedule.Policy.Overlap);
        var action = Assert.IsType<ScheduleActionStartWorkflow>(description.Schedule.Action);
        Assert.Equal(nameof(LivemapPollWorkflow), action.Workflow);
        Assert.Equal(TaskQueues.AgentPlatform, action.Options.TaskQueue);
    }

    [Fact]
    public async Task StartingAgain_UpdatesTheExistingSchedule_InsteadOfCreatingASecond()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        await RunOnceAsync(Service(env.Client, interval: TimeSpan.FromMinutes(30)));

        await RunOnceAsync(Service(env.Client, interval: TimeSpan.FromMinutes(5)));

        var description = await env.Client.GetScheduleHandle(LivemapScheduleService.ScheduleId).DescribeAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(description.Schedule.Spec.Intervals).Every);
        Assert.Equal([LivemapScheduleService.ScheduleId], await ScheduleIdsAsync(env.Client));
    }

    [Fact]
    public async Task WhenDisabled_NoScheduleIsCreated()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        await RunOnceAsync(Service(env.Client, enabled: false));

        Assert.Empty(await ScheduleIdsAsync(env.Client));
    }

    [Fact]
    public async Task WhenTurnedOff_TheScheduleOfAnEarlierRunIsRemoved()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        await RunOnceAsync(Service(env.Client));

        await RunOnceAsync(Service(env.Client, enabled: false));

        Assert.Empty(await ScheduleIdsAsync(env.Client));
    }

    [Fact]
    public async Task WhenTemporalIsNotReadyYet_TheServiceTriesAgain()
    {
        var interceptor = new FailFirstCreate();
        await using var env = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions { Interceptors = [interceptor] });

        await RunOnceAsync(Service(env.Client));

        Assert.Equal(2, interceptor.Attempts);
        Assert.Equal([LivemapScheduleService.ScheduleId], await ScheduleIdsAsync(env.Client));
    }
}
