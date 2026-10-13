using System.Globalization;

namespace AgentPlatform.Host.GitHub;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHub";

    public const string ReadAppSectionName = "GitHub:ReadApp";

    public string AppId { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;

    public static long RequireAppId(string? appId, string section = SectionName) =>
        long.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new InvalidOperationException($"{section}:AppId is required and must be the numeric GitHub App id.");
}
