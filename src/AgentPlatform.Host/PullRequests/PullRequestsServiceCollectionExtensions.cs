using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.PullRequests;

public static class PullRequestsServiceCollectionExtensions
{
    public static IServiceCollection AddPullRequests(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(UntrustedTextScannerOptions.SectionName);
        Validate(section.Get<UntrustedTextScannerOptions>() ?? new UntrustedTextScannerOptions());

        services.Configure<UntrustedTextScannerOptions>(section);
        services.TryAddSingleton(TimeProvider.System);
#pragma warning disable EXTEXP0001 // Experimental API: the standard handler's 10s attempt timeout and retries would cut a scan short and resend it
        services.AddHttpClient(UntrustedTextScannerClient.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<IUntrustedTextScanner, UntrustedTextScannerClient>();
        services.AddTransient<ICheckRunReporter, CheckRunReporter>();
        return services;
    }

    private static void Validate(UntrustedTextScannerOptions scanner)
    {
        if (!Uri.TryCreate(scanner.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("TextScanner:BaseUrl must be an absolute http or https URL.");
        }

        RequirePositive(scanner.RequestTimeout, nameof(scanner.RequestTimeout));
        RequirePositive(scanner.RetryBudget, nameof(scanner.RetryBudget));
        RequirePositive(scanner.RetryDelay, nameof(scanner.RetryDelay));
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{UntrustedTextScannerOptions.SectionName}:{name} must be positive.");
        }
    }
}
