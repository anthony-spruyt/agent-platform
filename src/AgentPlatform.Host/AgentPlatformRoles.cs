using Microsoft.Extensions.Configuration;

namespace AgentPlatform.Host;

public static class AgentPlatformRoles
{
    public const string ConfigurationKey = "AgentPlatform:Role";

    public static AgentPlatformRole Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConfigurationKey]?.Trim();
        foreach (var role in Enum.GetValues<AgentPlatformRole>())
        {
            if (string.Equals(role.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                return role;
            }
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} must be one of: {string.Join(", ", Enum.GetNames<AgentPlatformRole>())}.");
    }
}
