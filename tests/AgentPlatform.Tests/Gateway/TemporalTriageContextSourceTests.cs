using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.RenovateTriage;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace AgentPlatform.Tests.Gateway;

public class TemporalTriageContextSourceTests
{
    private const string Queue = "ctx-queue";

    private static readonly string[] _sections = ["facts", "block"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

#pragma warning disable CA1822 // Temporal requires the run method to be an instance method
    [Workflow]
    public class ContextWorkflow
    {
        private TriageContext? _context;

        [WorkflowQuery(TriageContextQueries.GetTriageContext)]
        public TriageContext? Context() => _context;

        [WorkflowSignal]
        public Task SetAsync(string[] sections)
        {
            _context = new TriageContext(sections);
            return Task.CompletedTask;
        }

        [WorkflowRun]
        public Task RunAsync() => Workflow.WaitConditionAsync(() => false);
    }

    [Workflow]
    public class NoQueryWorkflow
    {
        [WorkflowRun]
        public Task RunAsync() => Workflow.WaitConditionAsync(() => false);
    }

#pragma warning restore CA1822

    private static async Task<T> WithWorkflowsAsync<T>(Func<WorkflowEnvironment, Task<T>> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions(Queue).AddWorkflow<ContextWorkflow>().AddWorkflow<NoQueryWorkflow>());
        return await worker.ExecuteAsync(() => body(env), Ct);
    }

    private static AgentRunIdentity Identity<T>(WorkflowHandle<T> handle) => new(handle.Id, handle.ResultRunId!, "triage", 1);

    [Fact]
    public async Task ReadsTheContextTheNamedRunHolds()
    {
        var context = await WithWorkflowsAsync(async env =>
        {
            var handle = await env.Client.StartWorkflowAsync((ContextWorkflow wf) => wf.RunAsync(), new WorkflowOptions($"ctx-{Guid.NewGuid()}", Queue));
            await handle.SignalAsync(wf => wf.SetAsync(_sections));

            return await new TemporalTriageContextSource(env.Client).GetAsync(Identity(handle), Ct);
        });

        Assert.Equal(_sections, context!.Sections);
    }

    [Fact]
    public async Task ARunThatHoldsNoContextYet_ReadsAsNone()
    {
        var context = await WithWorkflowsAsync(async env =>
        {
            var handle = await env.Client.StartWorkflowAsync((ContextWorkflow wf) => wf.RunAsync(), new WorkflowOptions($"ctx-{Guid.NewGuid()}", Queue));

            return await new TemporalTriageContextSource(env.Client).GetAsync(Identity(handle), Ct);
        });

        Assert.Null(context);
    }

    [Fact]
    public async Task AWorkflowWithoutTheQuery_ReadsAsNone()
    {
        var context = await WithWorkflowsAsync(async env =>
        {
            var handle = await env.Client.StartWorkflowAsync((NoQueryWorkflow wf) => wf.RunAsync(), new WorkflowOptions($"ctx-{Guid.NewGuid()}", Queue));

            return await new TemporalTriageContextSource(env.Client).GetAsync(Identity(handle), Ct);
        });

        Assert.Null(context);
    }

    [Fact]
    public async Task AnUnknownRun_IsNotActive()
    {
        await WithWorkflowsAsync(async env =>
        {
            await Assert.ThrowsAsync<RunNotActiveException>(() =>
                new TemporalTriageContextSource(env.Client).GetAsync(new AgentRunIdentity("no-such-workflow", "run", "triage", 1), Ct));
            return 0;
        });
    }
}
