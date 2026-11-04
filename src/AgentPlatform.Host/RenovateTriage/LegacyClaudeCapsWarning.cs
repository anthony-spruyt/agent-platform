using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Host.RenovateTriage;

// Nothing binds these keys, so a deploy still setting them would run on the ClaudeBudget:* defaults without a sign.
public sealed partial class LegacyClaudeCapsWarning(IConfiguration configuration, ILogger<LegacyClaudeCapsWarning> logger) : IHostedService
{
    private const string LegacySection = "RenovateTriage:Claude";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetSection(LegacySection).AsEnumerable().Any(pair => pair.Value is not null))
        {
            LogLegacyCaps(logger, LegacySection, RenovateBudget.Kind);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Section}:* is no longer read, so the Claude caps are on their defaults. Set ClaudeBudget:MaxPerHour, ClaudeBudget:MaxPerDay and ClaudeBudget:Kinds:{Kind}:MaxPerSubjectPerDay instead.")]
    private static partial void LogLegacyCaps(ILogger logger, string section, string kind);
}
