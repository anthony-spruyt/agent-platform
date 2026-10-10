using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.Agents;

public static class AgentPodNaming
{
    private const string Prefix = "ap-";
    private const int MaxSlugLength = 40;
    private const int HashLength = 10;

    public static string PodName(string workflowId, string runId, string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{workflowId.Length}:{workflowId}|{runId.Length}:{runId}|{step.Length}:{step}"));
        var hash = Convert.ToHexStringLower(digest)[..HashLength];
        var slug = Slug($"{workflowId}-{step}");

        return slug.Length == 0 ? $"{Prefix}{hash}" : $"{Prefix}{slug}-{hash}";
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            var keep = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (keep)
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString();
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength];
        }

        return slug.Trim('-');
    }
}
