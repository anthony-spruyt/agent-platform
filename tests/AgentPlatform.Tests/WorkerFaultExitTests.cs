using System.Net;
using System.Net.Sockets;
using AgentPlatform.Host;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests;

public class WorkerFaultExitTests
{
    private sealed class RecordingExitCodeSetter : IExitCodeSetter
    {
        private readonly TaskCompletionSource<int> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Code => _code.Task;

        public void Apply(int exitCode) => _code.TrySetResult(exitCode);
    }

    // Holds each connection in the backlog until Release, so the fault cannot stop (and dispose) the host
    // while WebApplicationFactory is still starting it. Release then closes every connection the workers open.
    private sealed class FaultingTemporalEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task _closing = Task.CompletedTask;

        public FaultingTemporalEndpoint() => _listener.Start();

        public string Address => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public void Release() => _closing = Task.Run(CloseConnectionsAsync);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _closing;
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task CloseConnectionsAsync()
        {
            try
            {
                while (true)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Worker_WhenTemporalClosesConnection_SetsNonZeroExitCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var exitCode = new RecordingExitCodeSetter();
        await using var temporal = new FaultingTemporalEndpoint();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Worker");
            builder.UseSetting("AgentGateway:SessionTokenKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("ConnectionStrings:rundata", "Host=localhost;Database=agent_platform");
            builder.UseSetting("GitHub:AppId", "12345");
            builder.UseSetting("GitHub:PrivateKey", GitHubPayloads.PrivateKeyPem);
            builder.UseSetting("Temporal:Address", temporal.Address);
            builder.ConfigureTestServices(services => services.AddSingleton<IExitCodeSetter>(exitCode));
        });

        using var client = factory.CreateClient();
        temporal.Release();

        Assert.Equal(1, await exitCode.Code.WaitAsync(TimeSpan.FromSeconds(20), ct));
    }

    [Fact]
    public async Task Worker_WithoutFault_LeavesExitCodeUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        var exitCode = new RecordingExitCodeSetter();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Worker");
            builder.UseSetting("AgentGateway:SessionTokenKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("ConnectionStrings:rundata", "Host=localhost;Database=agent_platform");
            builder.UseSetting("GitHub:AppId", "12345");
            builder.UseSetting("GitHub:PrivateKey", GitHubPayloads.PrivateKeyPem);
            builder.UseSetting("Temporal:Address", env.Client.Connection.Options.TargetHost);
            builder.ConfigureTestServices(services => services.AddSingleton<IExitCodeSetter>(exitCode));
        });

        using var client = factory.CreateClient();
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await factory.DisposeAsync();

        Assert.False(exitCode.Code.IsCompleted);
    }
}
