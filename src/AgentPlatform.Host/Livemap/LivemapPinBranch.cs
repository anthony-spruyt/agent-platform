using System.Text.RegularExpressions;

namespace AgentPlatform.Host.Livemap;

internal static partial class LivemapPinBranch
{
    public static string For(string tag) =>
        LivemapPin.IsReleaseTag(tag) ? $"livemap/pin-{tag}" : throw new ArgumentException("The tag must be a plain release tag.", nameof(tag));

    public static bool IsMatch(string? branch) => branch is not null && Pattern().IsMatch(branch);

    [GeneratedRegex(@"^livemap/pin-v[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex Pattern();
}
