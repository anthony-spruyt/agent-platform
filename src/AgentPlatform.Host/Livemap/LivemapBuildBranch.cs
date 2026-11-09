using System.Globalization;

namespace AgentPlatform.Host.Livemap;

internal static class LivemapBuildBranch
{
    private const string Prefix = "livemap/build-";

    public static string For(long buildId) => $"{Prefix}{buildId.ToString(CultureInfo.InvariantCulture)}";

    public static bool IsMatch(string? branch) =>
        branch is not null
        && branch.StartsWith(Prefix, StringComparison.Ordinal)
        && branch.Length > Prefix.Length
        && branch.AsSpan(Prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
}
