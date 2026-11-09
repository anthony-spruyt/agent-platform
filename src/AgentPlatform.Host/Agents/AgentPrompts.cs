using System.Globalization;
using System.Reflection;

namespace AgentPlatform.Host.Agents;

public static class AgentPrompts
{
    public const string RenovateTriage = "renovate-triage";

    public const string LivemapHardPatch = "livemap-hard-patch";

    private const string PullRequestPlaceholder = "{{pr}}";

    public static bool Exists(string name) =>
        typeof(AgentPrompts).Assembly.GetManifestResourceInfo(ResourceName(name)) is not null;

    public static string Load(string name)
    {
        using var stream = typeof(AgentPrompts).Assembly.GetManifestResourceStream(ResourceName(name))
            ?? throw new InvalidOperationException($"The prompt {name} is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Render(string template, AgentRepoContext repo)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(repo);
        if (repo.PullRequest is null && template.Contains(PullRequestPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The prompt names a pull request, but the run has none.");
        }

        return template
            .Replace("{{repo}}", repo.Repo, StringComparison.Ordinal)
            .Replace(PullRequestPlaceholder, repo.PullRequest?.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{head_sha}}", repo.HeadSha, StringComparison.Ordinal);
    }

    private static string ResourceName(string name) => $"Prompts/{name}.md";
}
