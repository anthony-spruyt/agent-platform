using System.Text;
using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Host.RenovateTriage;

public static class PrDiff
{
    private static readonly string[] _lockFileNames =
    [
        "package-lock.json",
        "npm-shrinkwrap.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "bun.lockb",
        "go.sum",
        "uv.lock",
        "packages.lock.json",
        "flake.lock",
    ];

    public static string Build(IReadOnlyList<GitHubPullRequestFile> files, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        var diff = new StringBuilder();
        var bytes = 0;
        foreach (var file in files.OrderBy(IsLockFile))
        {
            if (bytes >= maxBytes)
            {
                break;
            }

            var section = string.IsNullOrEmpty(file.Patch)
                ? $"### {file.Filename} (no patch)\n\n"
                : $"### {file.Filename}\n{file.Patch}\n\n";
            diff.Append(section);
            bytes += Encoding.UTF8.GetByteCount(section);
        }

        return diff.ToString();
    }

    private static bool IsLockFile(GitHubPullRequestFile file)
    {
        var name = file.Filename[(file.Filename.LastIndexOf('/') + 1)..];
        return name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lock.hcl", StringComparison.OrdinalIgnoreCase)
            || _lockFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
