using System.Collections.Frozen;

namespace AgentPlatform.Host.Agents;

public static class ReadTokenPermissions
{
    private const string ReadLevel = "read";

    public static readonly FrozenDictionary<string, string> Default = new Dictionary<string, string>
    {
        ["contents"] = ReadLevel,
        ["pull_requests"] = ReadLevel,
        ["checks"] = ReadLevel,
        ["statuses"] = ReadLevel,
        ["issues"] = ReadLevel,
        ["actions"] = ReadLevel,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static void EnsureNarrowsDefault(IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0 || permissions.Any(p => !Default.ContainsKey(p.Key) || p.Value != ReadLevel))
        {
            throw new AgentConfigurationException("The token permission override must name at least one permission of the default read set, each at the read level.");
        }
    }
}
