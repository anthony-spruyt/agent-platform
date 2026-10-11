using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.GitHub;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubWebhookIntake(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GitHubWebhookOptions.SectionName);
        var options = section.Get<GitHubWebhookOptions>() ?? new GitHubWebhookOptions();
        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            throw new InvalidOperationException("GitHub:WebhookSecret is required.");
        }

        services.Configure<GitHubWebhookOptions>(section);
        services.AddSingleton<IRenovateTriageSignaler, TemporalRenovateTriageSignaler>();
        return services;
    }

    public static IServiceCollection AddGitHubAppClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GitHubAppOptions.SectionName);
        Validate(section.Get<GitHubAppOptions>() ?? new GitHubAppOptions());

        services.Configure<GitHubAppOptions>(section);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(GitHubHttp.ClientName, GitHubHttp.Configure);
        services.AddSingleton<IGitHubInstallationTokenProvider, GitHubInstallationTokenProvider>();
        services.AddHttpClient<IGitHubApiClient, GitHubApiClient>(GitHubHttp.Configure);
        return services;
    }

    private static void Validate(GitHubAppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            throw new InvalidOperationException("GitHub:AppId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            throw new InvalidOperationException("GitHub:PrivateKey is required.");
        }

        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(options.PrivateKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException("GitHub:PrivateKey must be an RSA private key in PEM format.", ex);
        }
    }
}
