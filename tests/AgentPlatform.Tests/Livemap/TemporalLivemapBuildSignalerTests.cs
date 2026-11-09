using AgentPlatform.Host.Livemap;
using Temporalio.Api.Enums.V1;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class TemporalLivemapBuildSignalerTests
{
    private static readonly LivemapBuildWake Wake = new(Sha, 77);

    private static async Task<List<string>> WakeSignalsAsync(WorkflowEnvironment env)
    {
        var history = await env.Client.GetWorkflowHandle(LivemapBuildWorkflow.WorkflowId(BuildId)).FetchHistoryAsync();
        return history.Events
            .Where(e => e.EventType == EventType.WorkflowExecutionSignaled
                && e.WorkflowExecutionSignaledEventAttributes.SignalName == LivemapBuildWorkflow.WakeSignalName)
            .Select(e => e.WorkflowExecutionSignaledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8())
            .ToList();
    }

    [Fact]
    public async Task ARunningBuildWorkflow_ReceivesTheWake()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await new TemporalLivemapBuildStarter(env.Client).StartAsync(BuildId, Gid, Ct);

        var delivered = await new TemporalLivemapBuildSignaler(env.Client).SignalAsync(BuildId, Wake, Ct);

        Assert.True(delivered);
        var signal = Assert.Single(await WakeSignalsAsync(env));
        Assert.Contains(Sha, signal, StringComparison.Ordinal);
        Assert.Contains("77", signal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoWorkflowForTheBuild_NothingIsDelivered_AndNothingFails()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();

        Assert.False(await new TemporalLivemapBuildSignaler(env.Client).SignalAsync(BuildId, Wake, Ct));
    }

    [Fact]
    public async Task AWorkflowThatHasEnded_IsNotWoken_AndNothingFails()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await new TemporalLivemapBuildStarter(env.Client).StartAsync(BuildId, Gid, Ct);
        await env.Client.GetWorkflowHandle(LivemapBuildWorkflow.WorkflowId(BuildId)).TerminateAsync();

        Assert.False(await new TemporalLivemapBuildSignaler(env.Client).SignalAsync(BuildId, Wake, Ct));
    }

    [Fact]
    public async Task TheWakeIsAddressedToTheBuildItNames()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await new TemporalLivemapBuildStarter(env.Client).StartAsync(BuildId, Gid, Ct);

        Assert.False(await new TemporalLivemapBuildSignaler(env.Client).SignalAsync(BuildId + 1, Wake, Ct));
        Assert.Empty(await WakeSignalsAsync(env));
    }
}
