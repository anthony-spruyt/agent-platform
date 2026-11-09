using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

public sealed partial class LivemapGitHub(HttpClient http, IGitHubInstallationTokenProvider tokens, IOptions<LivemapOptions> options) : ILivemapGitHub
{
    public const int MaxArtifactBytes = 1024 * 1024;

    private const string DispatchRef = "main";

    private const string ArtifactName = "livemap-compat";

    private const string BuildsPath = "data/builds.json";

    private static readonly Dictionary<string, string> _actionsWrite = new() { ["actions"] = "write" };
    private static readonly Dictionary<string, string> _actionsRead = new() { ["actions"] = "read" };
    private static readonly Dictionary<string, string> _contentsRead = new() { ["contents"] = "read" };

    public async Task<long> DispatchAsync(LivemapWorkflowFile workflow, long buildId, string manifestGid, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        if (!SteamIds.IsManifestGid(manifestGid))
        {
            throw new ArgumentException("The manifest id must be a Steam manifest gid.", nameof(manifestGid));
        }

        var (owner, name) = Fork();
        var body = new JsonObject
        {
            ["ref"] = DispatchRef,
            ["inputs"] = new JsonObject
            {
                ["build_id"] = buildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["manifest_gid"] = manifestGid,
            },
            ["return_run_details"] = true,
        };
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _actionsWrite, cancellationToken).ConfigureAwait(false);
        using var request = GitHubHttp.Request(
            HttpMethod.Post,
            $"repos/{owner}/{name}/actions/workflows/{FileName(workflow)}/dispatches",
            token,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        GitHubHttp.EnsureSuccess(response, "workflow dispatch");

        var dispatched = await ReadAsync<DispatchDto>(response, "workflow dispatch", cancellationToken).ConfigureAwait(false);
        return dispatched.WorkflowRunId is > 0 and var runId
            ? runId
            : throw new GitHubApiException("GitHub workflow dispatch returned no run id.", response.StatusCode);
    }

    public async Task<LivemapRun?> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _actionsRead, cancellationToken).ConfigureAwait(false);
        var run = await GitHubHttp.SendOrNullAsync<RunDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/actions/runs/{runId}", token),
            "workflow run read",
            cancellationToken).ConfigureAwait(false);
        return run is null ? null : new LivemapRun(run.Id, run.Status ?? string.Empty, run.Conclusion, run.HeadSha ?? string.Empty, run.CheckSuiteId ?? 0);
    }

    public async Task<CompatArtifact?> ReadCompatArtifactAsync(long runId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _actionsRead, cancellationToken).ConfigureAwait(false);
        var list = await GitHubHttp.SendOrNullAsync<ArtifactListDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/actions/runs/{runId}/artifacts?name={ArtifactName}", token),
            "artifact list",
            cancellationToken).ConfigureAwait(false);
        var live = (list?.Artifacts ?? []).Where(a => a.Name == ArtifactName && !a.Expired).ToList();
        if (live.Count == 0)
        {
            return null;
        }

        if (live.Count > 1)
        {
            throw new CompatArtifactException("The run holds more than one live compat artifact.");
        }

        if (live[0].SizeInBytes > MaxArtifactBytes)
        {
            throw new CompatArtifactException("The compat artifact is over the size cap.");
        }

        var zip = await DownloadAsync($"repos/{owner}/{name}/actions/artifacts/{live[0].Id}/zip", token, cancellationToken).ConfigureAwait(false);
        return zip is null ? null : CompatArtifactZip.Parse(zip);
    }

    public async Task<string?> ReadBuildsJsonAsync(string sha, CancellationToken cancellationToken)
    {
        if (sha is null || !CommitSha().IsMatch(sha))
        {
            throw new ArgumentException("The commit must be given as a lowercase hex sha.", nameof(sha));
        }

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var request = GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/contents/{BuildsPath}?ref={sha}", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        var text = await GitHubHttp.ReadTextOrNullAsync(http, request, "builds read", CompatArtifactZip.MaxFileBytes + 1, keepTail: false, cancellationToken).ConfigureAwait(false);
        return text is { Length: > CompatArtifactZip.MaxFileBytes }
            ? throw new CompatArtifactException("The builds file is over the size cap.")
            : text;
    }

    // The zip answer redirects to a pre-signed address on another host, so redirects are followed here and the token stays on the first hop.
    private async Task<byte[]?> DownloadAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var first = await http.SendAsync(GitHubHttp.Request(HttpMethod.Get, path, token), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        if (first.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || first.Headers.Location is not { IsAbsoluteUri: true } location
            || location.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubApiException($"GitHub artifact download answered {(int)first.StatusCode} instead of an https redirect.", first.StatusCode);
        }

        using var blob = new HttpRequestMessage(HttpMethod.Get, location);
        using var response = await http.SendAsync(blob, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        GitHubHttp.EnsureSuccess(response, "artifact download");
        return await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxArtifactBytes)
                {
                    throw new CompatArtifactException("The compat artifact download is over the size cap.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string FileName(LivemapWorkflowFile workflow) => workflow switch
    {
        LivemapWorkflowFile.ExeCapture => "exe-capture.yaml",
        LivemapWorkflowFile.Compat => "compat.yaml",
        _ => throw new ArgumentOutOfRangeException(nameof(workflow), workflow, "Unknown workflow file."),
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(GitHubJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubApiException($"GitHub {operation} returned an empty body.", response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException($"GitHub {operation} returned an unreadable body.", response.StatusCode, ex);
        }
    }

    private (string Owner, string Name) Fork() => GitHubRepoName.Parse(options.Value.Repo, nameof(LivemapOptions.Repo));

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex CommitSha();

    private sealed record ArtifactListDto(List<ArtifactDto>? Artifacts);

    private sealed record ArtifactDto(long Id, string? Name, bool Expired, long SizeInBytes);

    private sealed record DispatchDto(long? WorkflowRunId);

    private sealed record RunDto(long Id, string? Status, string? Conclusion, string? HeadSha, long? CheckSuiteId);
}
