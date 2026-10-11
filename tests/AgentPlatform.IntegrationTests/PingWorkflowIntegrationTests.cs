using Aspire.Hosting.Testing;
using AgentPlatform.Host.Workflows;
using Temporalio.Client;

namespace AgentPlatform.IntegrationTests;

public class PingWorkflowIntegrationTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task PingWorkflow_CompletesOnTheWorkerStartedByTheAppHost()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(StartupTimeout);
        var ct = timeout.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AgentPlatform_AppHost>(ct);
        await using var app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("worker", ct);

        var client = await ConnectAsync(app.GetEndpoint("temporal", "grpc"), ct);
        var result = await client.ExecuteWorkflowAsync(
            (PingWorkflow wf) => wf.RunAsync("aspire"),
            new WorkflowOptions($"ping-{Guid.NewGuid()}", TaskQueues.AgentPlatform));

        Assert.Equal("pong: aspire", result);
    }

    private static async Task<TemporalClient> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await TemporalClient.ConnectAsync(new($"{endpoint.Host}:{endpoint.Port}"));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
