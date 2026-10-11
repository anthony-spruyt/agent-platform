using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Temporalio.Client;

namespace AgentPlatform.IntegrationTests;

public sealed class AppHostFixture : IAsyncLifetime
{
    public const string TemporalNamespace = "agent-platform";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    private DistributedApplication? _app;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("The AppHost has not started.");

    public async ValueTask InitializeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(StartupTimeout);
        var ct = timeout.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AgentPlatform_AppHost>(ct);
        _app = await appHost.BuildAsync(ct);
        await _app.StartAsync(ct);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("worker", ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public async Task<string> RunDataConnectionStringAsync(CancellationToken ct) =>
        await App.GetConnectionStringAsync("rundata", ct)
        ?? throw new InvalidOperationException("The rundata connection string is not available.");

    public async Task<TemporalClient> ConnectAsync(CancellationToken ct)
    {
        var endpoint = App.GetEndpoint("temporal", "grpc");
        while (true)
        {
            try
            {
                return await TemporalClient.ConnectAsync(new($"{endpoint.Host}:{endpoint.Port}") { Namespace = TemporalNamespace });
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
