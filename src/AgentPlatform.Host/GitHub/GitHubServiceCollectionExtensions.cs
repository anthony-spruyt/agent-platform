using System.Security.Cryptography;
using AgentPlatform.Host.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.GitHub;

public static class GitHubServiceCollectionExtensions
{
    private const string ReadAppKey = "read-app";

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

        GitHubAppOptions.RequireAppId(options.AppId);

        services.Configure<GitHubWebhookOptions>(section);
        services.AddSingleton<IRenovateTriageSignaler, TemporalRenovateTriageSignaler>();
        return services;
    }

    public static IServiceCollection AddGitHubAppClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GitHubAppOptions.SectionName);
        var options = section.Get<GitHubAppOptions>() ?? new GitHubAppOptions();
        GitHubAppOptions.RequireAppId(options.AppId);
        Validate(options);

        services.Configure<GitHubAppOptions>(section);
        services.TryAddSingleton(TimeProvider.System);
        AddGitHubHttpClient(services);
        services.AddSingleton<GitHubInstallationTokenProvider>();
        services.AddSingleton<IGitHubInstallationTokenProvider>(provider => provider.GetRequiredService<GitHubInstallationTokenProvider>());
        services.AddSingleton<IGitHubAppJwtSource>(provider => provider.GetRequiredService<GitHubInstallationTokenProvider>());
        services.AddHttpClient<IGitHubApiClient, GitHubApiClient>(GitHubHttp.Configure);
        services.AddHttpClient<IGitHubAppClient, GitHubAppClient>(GitHubHttp.Configure);
        return services;
    }

    public static IServiceCollection AddGitHubReadApp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(GitHubAppOptions.ReadAppSectionName).Get<GitHubAppOptions>() ?? new GitHubAppOptions();
        if (string.IsNullOrWhiteSpace(options.AppId) && string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            return services;
        }

        Validate(options, GitHubAppOptions.ReadAppSectionName);
        services.TryAddSingleton(TimeProvider.System);
        AddGitHubHttpClient(services);
        services.AddKeyedSingleton<IGitHubTokenMinter>(
            ReadAppKey,
            (provider, _) => new GitHubInstallationTokenProvider(
                provider.GetRequiredService<IHttpClientFactory>(),
                Microsoft.Extensions.Options.Options.Create(options),
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRunSecretContributor>(provider =>
            new ReadAppSecretContributor(provider.GetRequiredKeyedService<IGitHubTokenMinter>(ReadAppKey)));
        return services;
    }

    private static void AddGitHubHttpClient(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(GitHubHttpClientMarker)))
        {
            return;
        }

        services.AddSingleton<GitHubHttpClientMarker>();
        services.AddHttpClient(GitHubHttp.ClientName, GitHubHttp.Configure);
    }

    private static void Validate(GitHubAppOptions options, string section = GitHubAppOptions.SectionName)
    {
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            throw new InvalidOperationException($"{section}:AppId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            throw new InvalidOperationException($"{section}:PrivateKey is required.");
        }

        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(options.PrivateKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException($"{section}:PrivateKey must be an RSA private key in PEM format.", ex);
        }
    }
}

internal sealed class GitHubHttpClientMarker;
