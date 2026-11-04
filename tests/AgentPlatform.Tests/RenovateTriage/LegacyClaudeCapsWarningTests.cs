using AgentPlatform.Host;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Tests.RenovateTriage;

public class LegacyClaudeCapsWarningTests
{
    private static async Task<List<(LogLevel Level, string Message)>> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AgentGateway:SessionTokenKey"] = "0123456789abcdef0123456789abcdef",
            ["ConnectionStrings:rundata"] = "Host=localhost;Database=agent_platform",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        GitHubPayloads.AddWorkerSettings(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddAgentPlatformWorker(configuration);
        await using var provider = services.BuildServiceProvider();

        var warning = Assert.Single(provider.GetServices<IHostedService>().OfType<LegacyClaudeCapsWarning>());
        await warning.StartAsync(TestContext.Current.CancellationToken);

        return logs.Snapshot();
    }

    [Theory]
    [InlineData("RenovateTriage:Claude:MaxPerHour")]
    [InlineData("RenovateTriage:Claude:MaxPerDay")]
    [InlineData("RenovateTriage:Claude:MaxPerPrPerDay")]
    public async Task AnOldCapStillInTheConfiguration_IsWarnedAboutAtStartup_NamingTheKeysThatReplaceIt(string oldKey)
    {
        var logs = await StartAsync(new() { [oldKey] = "4" });

        var warning = Assert.Single(logs, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("RenovateTriage:Claude", StringComparison.Ordinal));
        Assert.Contains("ClaudeBudget:MaxPerHour", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ClaudeBudget:MaxPerDay", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ClaudeBudget:Kinds:RenovateTriage:MaxPerSubjectPerDay", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheOldCaps_NothingIsWarnedAbout()
    {
        var logs = await StartAsync(new() { ["ClaudeBudget:MaxPerHour"] = "4" });

        Assert.DoesNotContain(logs, entry => entry.Message.Contains("RenovateTriage:Claude", StringComparison.Ordinal));
    }

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public void Dispose()
        {
        }
    }
}
