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
        Validate(litellmSection.Get<LiteLlmOptions>() ?? new LiteLlmOptions(), triageSection.Get<RenovateTriageOptions>() ?? new RenovateTriageOptions());

        services.Configure<LiteLlmOptions>(litellmSection);
        services.Configure<RenovateTriageOptions>(triageSection);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<IJevClient, JevClient>(client => client.Timeout = _jevTimeout);
        services.AddSingleton<TriageVerdictStore>();
        return services;
    }

    private static void Validate(LiteLlmOptions litellm, RenovateTriageOptions triage)
    {
        if (!Uri.TryCreate(litellm.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("LiteLlm:BaseUrl must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(litellm.ApiKey))
        {
            throw new InvalidOperationException("LiteLlm:ApiKey is required.");
        }

        if (triage.Mode != TriageMode.Shadow)
        {
            throw new InvalidOperationException("RenovateTriage:Mode must be Shadow.");
        }
    }
}
