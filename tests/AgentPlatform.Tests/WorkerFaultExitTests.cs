using AgentPlatform.Host;
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

    [Fact]
    public async Task Worker_WithUnreachableTemporal_SetsNonZeroExitCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var exitCode = new RecordingExitCodeSetter();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Worker");
            builder.UseSetting("Temporal:Address", "127.0.0.1:1");
            builder.ConfigureTestServices(services => services.AddSingleton<IExitCodeSetter>(exitCode));
        });

        using var client = factory.CreateClient();

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
            builder.UseSetting("Temporal:Address", env.Client.Connection.Options.TargetHost);
            builder.ConfigureTestServices(services => services.AddSingleton<IExitCodeSetter>(exitCode));
        });

        using var client = factory.CreateClient();
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await factory.DisposeAsync();

        Assert.False(exitCode.Code.IsCompleted);
    }
}
