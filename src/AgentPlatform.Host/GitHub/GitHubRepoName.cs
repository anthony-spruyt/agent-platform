namespace AgentPlatform.Host.GitHub;

public static class GitHubRepoName
{
    public static bool TryParse(string? fullName, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        if (fullName is null)
        {
            return false;
        }

        var parts = fullName.Split('/');
        if (parts.Length != 2 || !IsSegment(parts[0]) || !IsSegment(parts[1]))
        {
            return false;
        }

        owner = parts[0];
        name = parts[1];
        return true;
    }

    public static (string Owner, string Name) Parse(string? fullName, string paramName) =>
        TryParse(fullName, out var owner, out var name)
            ? (owner, name)
            : throw new ArgumentException("The repository must be given as owner/name.", paramName);

    private static bool IsSegment(string segment) =>
        segment.Length > 0
        && segment is not ("." or "..")
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
