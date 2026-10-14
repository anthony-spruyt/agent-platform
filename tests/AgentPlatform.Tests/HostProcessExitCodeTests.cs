using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AgentPlatform.Tests;

public class HostProcessExitCodeTests
{
    [Fact]
    public async Task Worker_WithUnreachableTemporal_ExitsWithNonZeroProcessExitCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AgentPlatform.Host.dll"));
        startInfo.Environment["AgentPlatform__Role"] = "Worker";
        startInfo.Environment["AgentGateway__SessionTokenKey"] = "0123456789abcdef0123456789abcdef";
        startInfo.Environment["ConnectionStrings__rundata"] = "Host=localhost;Database=agent_platform";
        startInfo.Environment["GitHub__AppId"] = "12345";
        startInfo.Environment["GitHub__PrivateKey"] = NewPrivateKeyPem();
        startInfo.Environment["LiteLlm__BaseUrl"] = "http://litellm.example:4000";
        startInfo.Environment["LiteLlm__ApiKey"] = "test-key";
        startInfo.Environment["RenovateTriage__OwnerLogin"] = "test-owner";
        startInfo.Environment["Temporal__Address"] = UnusedLoopbackAddress();
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The host process did not start.");
        try
        {
            var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(ct), process.StandardError.ReadToEndAsync(ct));
            await process.WaitForExitAsync(ct);

            var text = string.Concat(await output);
            Assert.DoesNotContain("is required", text, StringComparison.Ordinal);
            Assert.True(process.ExitCode == 1, $"Exit code {process.ExitCode}: {text}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string NewPrivateKeyPem()
    {
        using var key = RSA.Create(2048);
        return key.ExportRSAPrivateKeyPem();
    }

    private static string UnusedLoopbackAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"127.0.0.1:{port}";
    }
}
