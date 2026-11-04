using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPlatform.Host.ClaudeBudget;

public static class ClaudeBudgetServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeBudget(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ClaudeBudgetOptions.SectionName);
        Validate(section.Get<ClaudeBudgetOptions>() ?? new ClaudeBudgetOptions());

        services.Configure<ClaudeBudgetOptions>(section);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ClaudeBudgetStore>();
        return services;
    }

    private static void Validate(ClaudeBudgetOptions options)
    {
        if (options.MaxPerHour < 1 || options.MaxPerDay < 1)
        {
            throw new InvalidOperationException("ClaudeBudget:MaxPerHour and MaxPerDay must each be at least 1.");
        }

        foreach (var (kind, caps) in options.Kinds)
        {
            if (caps.MaxPerSubjectPerDay < 1 || caps.MaxFailedAttemptsPerRevision < 1)
            {
                throw new InvalidOperationException($"ClaudeBudget:Kinds:{kind}:MaxPerSubjectPerDay and MaxFailedAttemptsPerRevision must each be at least 1.");
            }
        }
    }
}
