using AgentPlatform.Host.ClaudeBudget;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

public static class LivemapServiceCollectionExtensions
{
    private static readonly Uri _steamInfoBaseAddress = new("https://api.steamcmd.net/");

    private static readonly TimeSpan _steamTimeout = TimeSpan.FromSeconds(30);

    private const long MaxSteamResponseBytes = 4 * 1024 * 1024;

    // Both roles: the web role has no database, so everything that needs one stays in AddLivemap.
    public static IServiceCollection AddLivemapEvents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(LivemapOptions.SectionName);
        Validate(section.Get<LivemapOptions>() ?? new LivemapOptions());
        services.Configure<LivemapOptions>(section);
        services.TryAddSingleton<ILivemapWakeStarter, TemporalLivemapWakeStarter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitHubEventHandler, LivemapCheckSuiteHandler>());
        return services;
    }

    public static IServiceCollection AddLivemap(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLivemapEvents(configuration);
        services.PostConfigure<ClaudeBudgetOptions>(budget => budget.Kinds.TryAdd(LivemapBudget.Kind, LivemapBudget.DefaultCaps()));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<ISteamAppInfo, SteamAppInfoClient>(client =>
        {
            client.BaseAddress = _steamInfoBaseAddress;
            client.Timeout = _steamTimeout;
            client.MaxResponseContentBufferSize = MaxSteamResponseBytes;
        });
        services.AddHttpClient<ILivemapGitHub, LivemapGitHub>(GitHubHttp.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<ILivemapPullRequests, LivemapPullRequests>(GitHubHttp.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<ILivemapReleases, LivemapReleases>(GitHubHttp.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<ILivemapNotices, LivemapNotices>(GitHubHttp.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddTransient<LivemapReleaseVerifier>();
        services.AddTransient<LivemapPatchValidator>();
        services.AddHttpClient(nameof(ILivemapPinPullRequests), GitHubHttp.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddTypedClient<ILivemapPinPullRequests>((http, provider) => new LivemapPullRequests(
                http,
                provider.GetRequiredService<IGitHubInstallationTokenProvider>(),
                provider.GetRequiredService<IGitHubAppJwtSource>(),
                provider.GetRequiredService<IOptions<LivemapOptions>>(),
                LivemapRepoKind.Pin));
        services.AddSingleton<LivemapBuildStore>();
        services.TryAddSingleton<ILivemapBuildsSchema, LivemapBuildsSchemaValidator>();
        services.TryAddSingleton<ILivemapBuildStarter, TemporalLivemapBuildStarter>();
        services.TryAddSingleton<ILivemapBuildSignaler, TemporalLivemapBuildSignaler>();
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

        if (options.TrackingIssue < 1)
        {
            throw new InvalidOperationException("Livemap:TrackingIssue must be the number of the fork's tracking issue.");
        }

        if (!AreCheckNames(options.RequiredChecks))
        {
            throw new InvalidOperationException("Livemap:RequiredChecks must list at least one check run name, each trimmed, non-empty and given once.");
        }

        if (options.ReleaseBot is not { Length: > 0 } bot || bot != bot.Trim() || !bot.EndsWith("[bot]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Livemap:ReleaseBot must be the login of the fork's release bot, ending in [bot].");
        }

        if (!AreCheckNames(options.ReleaseRequiredChecks))
        {
            throw new InvalidOperationException("Livemap:ReleaseRequiredChecks must list at least one check run name, each trimmed, non-empty and given once.");
        }

        if (options.HardPath)
        {
            ValidateHardPath(options);
        }
    }

    private static void ValidateHardPath(LivemapOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AgentModel))
        {
            throw new InvalidOperationException("Livemap:AgentModel must name the model the hard-patch agent runs.");
        }

        if (options.AgentMaxTurns < 1)
        {
            throw new InvalidOperationException("Livemap:AgentMaxTurns must be at least 1.");
        }

        if (options.AgentMaxBudgetUsd <= 0)
        {
            throw new InvalidOperationException("Livemap:AgentMaxBudgetUsd must be greater than 0.");
        }

        if (options.AgentRunsPerBuild is < 1 or > LivemapOptions.MaxAgentRunsPerBuild)
        {
            throw new InvalidOperationException($"Livemap:AgentRunsPerBuild must be between 1 and {LivemapOptions.MaxAgentRunsPerBuild}.");
        }

        if (options.AgentDeadlineSeconds is < LivemapOptions.MinAgentDeadlineSeconds or > LivemapOptions.MaxAgentDeadlineSeconds)
        {
            throw new InvalidOperationException($"Livemap:AgentDeadlineSeconds must be between {LivemapOptions.MinAgentDeadlineSeconds} and {LivemapOptions.MaxAgentDeadlineSeconds}.");
        }

        if (!Uri.TryCreate(options.DepotDownloaderUrl, UriKind.Absolute, out var tool) || tool.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Livemap:DepotDownloaderUrl must be an https URL.");
        }

        if (!IsLowerSha256(options.DepotDownloaderSha256))
        {
            throw new InvalidOperationException("Livemap:DepotDownloaderSha256 must be 64 lower-case hex characters.");
        }

        if (!Uri.TryCreate(options.PythonIndexUrl, UriKind.Absolute, out var index) || index.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Livemap:PythonIndexUrl must be an http or https URL.");
        }

        if (!IsVersion(options.CapstoneVersion))
        {
            throw new InvalidOperationException("Livemap:CapstoneVersion must be an exact version: digits separated by dots.");
        }

        if (!IsLowerSha256(options.CapstoneSha256))
        {
            throw new InvalidOperationException("Livemap:CapstoneSha256 must be 64 lower-case hex characters.");
        }
    }

    private static bool IsVersion(string? value) =>
        !string.IsNullOrEmpty(value) && value.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit));

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool AreCheckNames(List<string> names) =>
        names.Count > 0
        && names.All(name => !string.IsNullOrEmpty(name) && name == name.Trim())
        && names.Distinct(StringComparer.Ordinal).Count() == names.Count;

    private static bool IsRepoRelativePath(string? path) =>
        path is { Length: > 0 }
        && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && segment.All(IsPathChar));

    private static bool IsPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+' or '~';
}
