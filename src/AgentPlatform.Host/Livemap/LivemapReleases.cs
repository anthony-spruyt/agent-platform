using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

// Each call asks for the installation token it needs and no more; the provenance read is public and sends none.
public sealed partial class LivemapReleases(HttpClient http, IGitHubInstallationTokenProvider tokens, IOptions<LivemapOptions> options) : ILivemapReleases
{
    private const string SlsaProvenance = "https://slsa.dev/provenance/v1";

    private const string ReleaseWorkflow = "release.yaml";

    private const int MaxAttestations = 10;

    private static readonly Dictionary<string, string> _contentsRead = new() { ["contents"] = "read" };
    private static readonly Dictionary<string, string> _actionsRead = new() { ["actions"] = "read" };
    private static readonly Dictionary<string, string> _actionsWrite = new() { ["actions"] = "write" };

    public async Task<LivemapRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var release = await GitHubHttp.SendOrNullAsync<ReleaseDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/releases/latest", token),
            "latest release read",
            cancellationToken).ConfigureAwait(false);
        return release is null ? null : ToRelease(release);
    }

    public async Task<LivemapRelease?> GetByTagAsync(string tag, CancellationToken cancellationToken)
    {
        RequireTag(tag);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var release = await GitHubHttp.SendOrNullAsync<ReleaseDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/releases/tags/{tag}", token),
            "release read",
            cancellationToken).ConfigureAwait(false);
        return release is null ? null : ToRelease(release);
    }

    // The tags/ prefix makes GitHub read the tag, not a branch of the same name.
    public async Task<string?> GetTagCommitAsync(string tag, CancellationToken cancellationToken)
    {
        RequireTag(tag);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var commit = await GitHubHttp.SendOrNullAsync<CommitDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/commits/tags/{tag}", token),
            "tag commit read",
            cancellationToken).ConfigureAwait(false);
        return commit is null ? null : LivemapCommitSha.IsMatch(commit.Sha) ? commit.Sha : throw new GitHubApiException("GitHub tag commit read returned no commit sha.", HttpStatusCode.OK);
    }

    public async Task<LivemapTagRun?> GetTagRunAsync(string tag, CancellationToken cancellationToken)
    {
        RequireTag(tag);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _actionsRead, cancellationToken).ConfigureAwait(false);
        var list = await GitHubHttp.SendOrNullAsync<RunListDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/actions/workflows/{ReleaseWorkflow}/runs?event=push&branch={tag}&per_page=1", token),
            "release run read",
            cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            return null;
        }

        var runs = list.WorkflowRuns ?? throw new GitHubApiException("GitHub release run read returned no runs array.", HttpStatusCode.OK);
        return runs.FirstOrDefault() is { } run
            ? new LivemapTagRun(run.Id, run.Status ?? string.Empty, run.Conclusion, run.RunAttempt, run.HeadSha ?? string.Empty, run.HtmlUrl ?? string.Empty)
            : null;
    }

    public async Task RerunFailedJobsAsync(long runId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _actionsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/actions/runs/{runId}/rerun-failed-jobs", token),
            "failed jobs rerun",
            cancellationToken).ConfigureAwait(false);
    }

    // The asset answer redirects to a pre-signed address on another host, so redirects are followed here and the token stays on the first hop.
    public async Task<string?> ReadAssetTextAsync(long assetId, int maxChars, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(assetId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        using var request = GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/releases/assets/{assetId}", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var first = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        if (first.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || first.Headers.Location is not { IsAbsoluteUri: true } location
            || location.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubApiException($"GitHub asset download answered {(int)first.StatusCode} instead of an https redirect.", first.StatusCode);
        }

        var text = await GitHubHttp.ReadTextOrNullAsync(http, new HttpRequestMessage(HttpMethod.Get, location), "asset download", maxChars + 1, keepTail: false, cancellationToken).ConfigureAwait(false);
        return text is not null && text.Length > maxChars
            ? throw new GitHubApiException("GitHub asset download is over the size cap.", HttpStatusCode.OK)
            : text;
    }

    // Public data: the attestation list of a public repository needs no token.
    public async Task<IReadOnlyList<LivemapProvenance>> GetProvenanceAsync(string sha256, CancellationToken cancellationToken)
    {
        if (!Digest().IsMatch(sha256 ?? string.Empty))
        {
            throw new ArgumentException("The digest must be a lowercase hex sha256.", nameof(sha256));
        }

        var (owner, name) = Fork();
        var list = await GitHubHttp.SendOrNullAsync<JsonNode>(
            http,
            new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{name}/attestations/sha256:{sha256}?per_page={MaxAttestations}"),
            "attestation read",
            cancellationToken).ConfigureAwait(false);
        return list?["attestations"] is JsonArray attestations
            ? [.. attestations.Select(attestation => ParseProvenance(Text(attestation?["bundle"]?["dsseEnvelope"]?["payload"]))).OfType<LivemapProvenance>()]
            : [];
    }

    private static LivemapRelease ToRelease(ReleaseDto release) => new(
        release.TagName ?? string.Empty,
        release.Draft,
        release.Prerelease,
        release.Immutable,
        release.HtmlUrl ?? string.Empty,
        [.. (release.Assets ?? []).Select(asset => new LivemapReleaseAsset(asset.Id, asset.Name ?? string.Empty, asset.Size, asset.Digest))]);

    private static LivemapProvenance? ParseProvenance(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            var statement = JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
            if (Text(statement?["predicateType"]) != SlsaProvenance)
            {
                return null;
            }

            var subjects = (statement?["subject"] as JsonArray ?? [])
                .Select(subject => (Name: Text(subject?["name"]), Digest: Text(subject?["digest"]?["sha256"])))
                .Where(subject => subject.Name is not null && subject.Digest is not null)
                .Select(subject => new LivemapProvenanceSubject(subject.Name!, subject.Digest!))
                .ToList();
            var build = statement?["predicate"]?["buildDefinition"];
            var workflow = build?["externalParameters"]?["workflow"];
            var commit = (build?["resolvedDependencies"] as JsonArray ?? []).Select(dependency => Text(dependency?["digest"]?["gitCommit"])).FirstOrDefault(sha => sha is not null);
            return new LivemapProvenance(subjects, Text(workflow?["path"]) ?? string.Empty, Text(workflow?["ref"]) ?? string.Empty, Text(workflow?["repository"]) ?? string.Empty, commit);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void RequireTag(string tag)
    {
        if (!LivemapPin.IsReleaseTag(tag))
        {
            throw new ArgumentException("The tag must be a plain release tag.", nameof(tag));
        }
    }

    private (string Owner, string Name) Fork() => GitHubRepoName.Parse(options.Value.Repo, nameof(LivemapOptions.Repo));

    [GeneratedRegex(@"^[a-f0-9]{64}\z")]
    private static partial Regex Digest();

    private sealed record ReleaseDto(string? TagName, bool Draft, bool Prerelease, bool Immutable, string? HtmlUrl, List<AssetDto>? Assets);

    private sealed record AssetDto(long Id, string? Name, long Size, string? Digest);

    private sealed record CommitDto(string? Sha);

    private sealed record RunListDto(List<RunDto>? WorkflowRuns);

    private sealed record RunDto(long Id, string? Status, string? Conclusion, int RunAttempt, string? HeadSha, string? HtmlUrl);
}
