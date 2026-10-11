using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AgentPlatform.Tests.GitHub;

public class TemporalRenovateTriageSignalerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CheckSuiteCompletedSignal Signal(int pr = 7, long suite = 1, string delivery = "d-1", string repo = "octo/widgets") =>
        new(repo, pr, "0123456789abcdef0123456789abcdef01234567", suite, "github-actions", "success", delivery);

    [Fact]
    public void TheWorkflowIdIsDerivedFromTheRepositoryAndPullRequest()
    {
        Assert.Equal("renovate-triage/octo/widgets/7", RenovateTriageWorkflow.WorkflowId("octo/widgets", 7));
    }

    [Fact]
    public async Task ASignal_StartsTheTriageWorkflowOnTheAgentPlatformQueueWithTheSignalAttached()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var signaler = new TemporalRenovateTriageSignaler(env.Client);

        await signaler.SignalAsync(Signal(), Ct);

        var handle = env.Client.GetWorkflowHandle("renovate-triage/octo/widgets/7");
        var description = await handle.DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
        Assert.Equal("RenovateTriageWorkflow", description.WorkflowType);
        Assert.Equal(TaskQueues.AgentPlatform, description.TaskQueue);
        Assert.Equal(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running, description.Status);

        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<RenovateTriageWorkflow>());
        var received = await worker.ExecuteAsync(
            () => env.Client.GetWorkflowHandle<RenovateTriageWorkflow, IReadOnlyList<CheckSuiteCompletedSignal>>("renovate-triage/octo/widgets/7").GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }),
            Ct);

        Assert.Equal(Signal(), Assert.Single(received));
    }

    [Fact]
    public async Task SignalsForThePullRequestWhileItsWorkflowIsRunning_ReachTheSameWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var signaler = new TemporalRenovateTriageSignaler(env.Client);

        await signaler.SignalAsync(Signal(suite: 1, delivery: "d-1"), Ct);
        await signaler.SignalAsync(Signal(suite: 2, delivery: "d-2"), Ct);

        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions(TaskQueues.AgentPlatform).AddWorkflow<RenovateTriageWorkflow>());
        var received = await worker.ExecuteAsync(
            () => env.Client.GetWorkflowHandle<RenovateTriageWorkflow, IReadOnlyList<CheckSuiteCompletedSignal>>("renovate-triage/octo/widgets/7").GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = Ct }),
            Ct);

        Assert.Equal(["d-1", "d-2"], received.Select(s => s.DeliveryId));
    }

    [Fact]
    public async Task AnotherPullRequestOrRepository_GetsItsOwnWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var signaler = new TemporalRenovateTriageSignaler(env.Client);

        await signaler.SignalAsync(Signal(pr: 7), Ct);
        await signaler.SignalAsync(Signal(pr: 8), Ct);
        await signaler.SignalAsync(Signal(pr: 7, repo: "octo/gadgets"), Ct);

        foreach (var id in new[] { "renovate-triage/octo/widgets/7", "renovate-triage/octo/widgets/8", "renovate-triage/octo/gadgets/7" })
        {
            var description = await env.Client.GetWorkflowHandle(id).DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = Ct } });
            Assert.Equal("RenovateTriageWorkflow", description.WorkflowType);
        }
    }

    [Fact]
    public async Task TheSignalCarriesEveryFieldAsJson()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await new TemporalRenovateTriageSignaler(env.Client).SignalAsync(Signal(), Ct);

        var history = await env.Client.GetWorkflowHandle("renovate-triage/octo/widgets/7").FetchHistoryAsync();
        var signalEvent = Assert.Single(history.Events, e => e.WorkflowExecutionSignaledEventAttributes is not null);

        Assert.Equal("CheckSuiteCompleted", signalEvent.WorkflowExecutionSignaledEventAttributes.SignalName);
        var json = signalEvent.WorkflowExecutionSignaledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
        Assert.Contains("\"repo\":\"octo/widgets\"", json, StringComparison.Ordinal);
        Assert.Contains("\"prNumber\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"headSha\":\"0123456789abcdef0123456789abcdef01234567\"", json, StringComparison.Ordinal);
        Assert.Contains("\"checkSuiteId\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"appSlug\":\"github-actions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"conclusion\":\"success\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deliveryId\":\"d-1\"", json, StringComparison.Ordinal);
    }
}
