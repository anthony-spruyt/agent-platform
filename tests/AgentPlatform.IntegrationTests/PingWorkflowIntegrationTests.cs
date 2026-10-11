using AgentPlatform.Host.RunData;
using AgentPlatform.Host.Workflows;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;

namespace AgentPlatform.IntegrationTests;

[Collection(AppHostSharedFixture.Name)]
public class PingWorkflowIntegrationTests(AppHostFixture fixture)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task PingWorkflow_CompletesOnTheWorkerStartedByTheAppHostAndRecordsARun()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var ct = timeout.Token;
        var workflowId = $"ping-{Guid.NewGuid()}";

        var client = await fixture.ConnectAsync(ct);
        var result = await client.ExecuteWorkflowAsync(
            (PingWorkflow wf) => wf.RunAsync("aspire"),
            new WorkflowOptions(workflowId, TaskQueues.AgentPlatform)
            {
                ExecutionTimeout = TimeSpan.FromMinutes(1),
                Rpc = new() { CancellationToken = ct },
            });

        Assert.Equal("pong: aspire", result);

        var connectionString = await fixture.RunDataConnectionStringAsync(ct);
        await using var db = new RunDataDbContext(new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql(connectionString).Options);
        var run = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.WorkflowId == workflowId, ct);
        Assert.Equal("PingWorkflow", run.WorkflowType);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAt);
        var step = Assert.Single(run.Steps);
        Assert.Equal(LadderRung.Rule, step.Rung);
        Assert.Equal(1.0, step.Confidence);
    }
}
