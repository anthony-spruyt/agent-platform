using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.Livemap;

public static class LivemapServiceCollectionExtensions
{
    private static readonly Uri _steamInfoBaseAddress = new("https://api.steamcmd.net/");

    private static readonly TimeSpan _steamTimeout = TimeSpan.FromSeconds(30);

    private const long MaxSteamResponseBytes = 4 * 1024 * 1024;

    public static IServiceCollection AddLivemap(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(LivemapOptions.SectionName);
        Validate(section.Get<LivemapOptions>() ?? new LivemapOptions());
        services.Configure<LivemapOptions>(section);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<ISteamAppInfo, SteamAppInfoClient>(client =>
        {
            client.BaseAddress = _steamInfoBaseAddress;
            client.Timeout = _steamTimeout;
            client.MaxResponseContentBufferSize = MaxSteamResponseBytes;
        });
        services.AddSingleton<LivemapBuildStore>();
        services.AddTransient<IShippedBuilds, ShippedBuildsReader>();
        services.AddHostedService<LivemapScheduleService>();
        return services;
    }

    private static void Validate(LivemapOptions options)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (!GitHubRepoName.TryParse(options.Repo, out _, out _))
        {
            throw new InvalidOperationException("Livemap:Repo must be given as owner/name.");
        }

        if (!GitHubRepoName.TryParse(options.PinRepo, out _, out _))
        {
            throw new InvalidOperationException("Livemap:PinRepo must be given as owner/name.");
        }

        if (!IsRepoRelativePath(options.PinPath))
        {
            throw new InvalidOperationException("Livemap:PinPath must be a relative path of plain segments.");
        }

        if (options.PollInterval < LivemapOptions.MinPollInterval)
        {
            throw new InvalidOperationException("Livemap:PollInterval must be at least one minute.");
        }
    }

    private static bool IsRepoRelativePath(string? path) =>
        path is { Length: > 0 }
        && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && segment.All(IsPathChar));

    private static bool IsPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+' or '~';
}
