using System.Globalization;
using System.Text;

namespace AgentPlatform.Host.Livemap;

public static class LivemapNotice
{
    public static string Marker(long buildId) => string.Create(CultureInfo.InvariantCulture, $"<!-- livemap-build:{buildId} -->");

    public static string Compose(LivemapBuild build, string fork, string pinRepo)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(fork);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinRepo);

        var shipped = build.State == LivemapBuildState.Pinned;
        if (!shipped && build.State != LivemapBuildState.NeedsOwner)
        {
            throw new ArgumentException("Only a pinned build or one that needs a person gets a notice.", nameof(build));
        }

        var text = new StringBuilder();
        text.Append(Marker(build.BuildId)).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"**Livemap build {build.BuildId}: {(shipped ? "shipped" : "needs a person")}**\n\n");
        text.Append(Result(build, shipped)).Append("\n\n");

        var links = new List<string>();
        if (build.CompatRunId is { } run)
        {
            links.Add(string.Create(CultureInfo.InvariantCulture, $"- Compat run: https://github.com/{fork}/actions/runs/{run}"));
        }

        if (build.PullRequestNumber is { } pull)
        {
            links.Add(string.Create(CultureInfo.InvariantCulture, $"- Data pull request: https://github.com/{fork}/pull/{pull}"));
        }

        if (build.ReleaseTag is { } tag)
        {
            links.Add($"- Release: https://github.com/{fork}/releases/tag/{tag}");
        }

        if (build.PinPullRequestNumber is { } pin)
        {
            links.Add(string.Create(CultureInfo.InvariantCulture, $"- Pin pull request: https://github.com/{pinRepo}/pull/{pin}"));
        }

        if (links.Count > 0)
        {
            text.Append("Links\n").AppendJoin('\n', links).Append("\n\n");
        }

        text.Append("Timings\n");
        text.Append(CultureInfo.InvariantCulture, $"- Detected: {build.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        AppendTiming(text, "Data pull request merged", build.CreatedAt, build.MergedAt);
        AppendTiming(text, "Released", build.CreatedAt, build.ReleasedAt);
        AppendTiming(text, "Pinned", build.CreatedAt, build.PinnedAt);
        return text.Append('\n').ToString();
    }

    private static string Result(LivemapBuild build, bool shipped)
    {
        if (!shipped)
        {
            return string.IsNullOrWhiteSpace(build.Detail) ? "The worker stopped on this build." : build.Detail;
        }

        return build.PinPullRequestNumber is null
            ? $"The pin already held {build.ReleaseTag} or a newer release, so it needed no pull request."
            : $"The pin now holds {build.ReleaseTag}.";
    }

    private static void AppendTiming(StringBuilder text, string label, DateTimeOffset start, DateTimeOffset? at)
    {
        if (at is { } moment)
        {
            var elapsed = moment - start;
            text.Append(CultureInfo.InvariantCulture, $"\n- {label}: {(int)elapsed.TotalHours}h {elapsed.Minutes:00}m after detection");
        }
    }
}
