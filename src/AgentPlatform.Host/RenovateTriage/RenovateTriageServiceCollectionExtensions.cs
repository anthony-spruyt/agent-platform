using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.RenovateTriage;

public static class RenovateTriageServiceCollectionExtensions
{
    private static readonly TimeSpan _jevTimeout = TimeSpan.FromSeconds(60);

    public static IServiceCollection AddRenovateTriage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var litellmSection = configuration.GetSection(LiteLlmOptions.SectionName);
        var triageSection = configuration.GetSection(RenovateTriageOptions.SectionName);
        Validate(litellmSection.Get<LiteLlmOptions>() ?? new LiteLlmOptions());
        Validate(triageSection.Get<RenovateTriageOptions>() ?? new RenovateTriageOptions()); // Binds eagerly so an unknown Mode fails at startup, not on first use

        services.Configure<LiteLlmOptions>(litellmSection);
        services.Configure<RenovateTriageOptions>(triageSection);
        services.TryAddSingleton(TimeProvider.System);
#pragma warning disable EXTEXP0001 // Experimental API: the standard handler's 10s attempt timeout and retries would resend a paid POST
        services.AddHttpClient<IJevClient, JevClient>(client => client.Timeout = _jevTimeout).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<TriageVerdictStore>();
        services.AddSingleton<TriagePendingStore>();
        services.AddSingleton<ClaudeBudgetStore>();
        services.AddSingleton<SweeperStore>();
        return services;
    }

    private static void Validate(RenovateTriageOptions triage)
    {
        var caps = triage.Claude;
        if (caps.MaxPerHour < 1 || caps.MaxPerDay < 1 || caps.MaxPerPrPerDay < 1)
        {
            throw new InvalidOperationException("RenovateTriage:Claude:MaxPerHour, MaxPerDay and MaxPerPrPerDay must each be at least 1.");
        }

        if (triage.Sweeper.Interval < SweeperOptions.MinInterval)
        {
            throw new InvalidOperationException("RenovateTriage:Sweeper:Interval must be at least one minute.");
        }
    }

    private static void Validate(LiteLlmOptions litellm)
    {
        if (!Uri.TryCreate(litellm.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("LiteLlm:BaseUrl must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(litellm.ApiKey))
        {
            throw new InvalidOperationException("LiteLlm:ApiKey is required.");
        }

        if (!IsRelativePath(baseUrl, litellm.JevPath))
        {
            throw new InvalidOperationException("LiteLlm:JevPath must be a relative path without a scheme, host, query, fragment, leading slash or dot segments.");
        }

        if (!IsPrintableWithoutWhitespace(litellm.JevModel))
        {
            throw new InvalidOperationException("LiteLlm:JevModel must be a non-empty value of printable characters without whitespace.");
        }
    }

    private static bool IsPrintableWithoutWhitespace(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c is > ' ' and <= '~');

    private static bool IsRelativePath(Uri baseUrl, string? path) =>
        path is not null
        && IsPrintableWithoutWhitespace(path)
        && path[0] is not ('/' or '\\')
        && path.AsSpan().IndexOfAny('?', '#') < 0
        && !path.Split('/', '\\')[0].Contains(':')
        && !path.Split('/', '\\').Any(segment => Uri.UnescapeDataString(segment) is "." or "..")
        && Uri.TryCreate(baseUrl, path, out var resolved)
        && resolved.Scheme == baseUrl.Scheme
        && resolved.Authority == baseUrl.Authority;
}
