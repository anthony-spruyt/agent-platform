using System.Globalization;
using System.Text.RegularExpressions;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Host.Livemap;

public sealed record LivemapHardPatchInputs(
    long BuildId,
    string ManifestGid,
    long PreviousBuildId,
    string PreviousRow,
    string Report,
    string BaseSha,
    string? Notes,
    int Run,
    int Runs);

public static partial class LivemapHardPatchPrompt
{
    public const string PreviousExeVolume = "exe";

    public const int MaxReportChars = 24_000;

    private const string ExeName = "enshrouded_server.exe";
    private const string CompatAsset = "livemap-compat-linux-x64";
    private const string NoNotes = "No earlier run left notes.";

    // One pass over the template: a value that looks like a placeholder is never looked at again.
    public static string Render(string template, LivemapOptions options, LivemapHardPatchInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        var values = Values(options, inputs);
        return PlaceholderPattern().Replace(
            template,
            match => values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new InvalidOperationException($"The prompt names the placeholder {match.Groups[1].Value}, which the run does not provide."));
    }

    private static Dictionary<string, string> Values(LivemapOptions options, LivemapHardPatchInputs inputs) =>
        new(StringComparer.Ordinal)
        {
            ["repo"] = options.Repo,
            ["base_sha"] = inputs.BaseSha,
            ["build_id"] = Number(inputs.BuildId),
            ["manifest_gid"] = inputs.ManifestGid,
            ["previous_build_id"] = Number(inputs.PreviousBuildId),
            ["previous_exe"] = $"{AgentPodSpecBuilder.ImageVolumePath(PreviousExeVolume)}/{ExeName}",
            ["run"] = Number(inputs.Run),
            ["runs"] = Number(inputs.Runs),
            ["app_id"] = SteamAppInfoClient.AppId,
            ["depot_id"] = SteamAppInfoClient.DepotId,
            ["depot_downloader_url"] = options.DepotDownloaderUrl,
            ["depot_downloader_sha256"] = options.DepotDownloaderSha256,
            ["pip_index_url"] = options.PythonIndexUrl,
            ["pip_trusted_host_arg"] = TrustedHostArgument(options.PythonIndexUrl),
            ["capstone_version"] = options.CapstoneVersion,
            ["capstone_sha256"] = options.CapstoneSha256,
            ["compat_url"] = $"https://github.com/{options.Repo}/releases/latest/download/{CompatAsset}",
            ["previous_row"] = Fenced(inputs.PreviousRow),
            ["report"] = Report(inputs.Report),
            ["notes"] = inputs.Notes is null ? NoNotes : Fenced(inputs.Notes),
        };

    // pip refuses an http index unless its host is trusted; for https the flag would switch certificate checks off, so it is left out.
    private static string TrustedHostArgument(string indexUrl)
    {
        var index = new Uri(indexUrl);
        return index.Scheme == Uri.UriSchemeHttp ? $"--trusted-host {index.Authority} " : string.Empty;
    }

    private static string Report(string report) =>
        report.Length > MaxReportChars
            ? $"{Fenced(report[..MaxReportChars])}\nThe report is cut at {Number(MaxReportChars)} characters."
            : Fenced(report);

    // The fence is longer than any run of backticks in the value, so the value cannot close it.
    private static string Fenced(string value)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(value) + 1));
        return $"{fence}\n{value}\n{fence}";
    }

    private static int LongestBacktickRun(string value)
    {
        int longest = 0, run = 0;
        foreach (var c in value)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlaceholderPattern();
}
