using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

internal static partial class LivemapPinBranch
{
    public static bool IsMatch(string? branch) => branch is not null && Pattern().IsMatch(branch);

    [GeneratedRegex(@"^livemap/pin-v[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex Pattern();
}
