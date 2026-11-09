using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

internal static partial class LivemapCommitSha
{
    public static bool IsMatch(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex Pattern();
}
