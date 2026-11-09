using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

// Models a fork whose release passes the check until a test changes one fact.
internal sealed class FakeLivemapReleases : ILivemapReleases
{
    public const string Fork = "octo/livemap";
    public const long ZipAssetId = 71;
    public const long ChecksumAssetId = 72;

    public static readonly string ZipSha = new('a', 64);
    public static readonly string TagCommit = new('d', 40);

    public LivemapRelease? Latest { get; set; }

    public Dictionary<string, LivemapRelease> Releases { get; } = [];

    public Dictionary<string, string> TagCommits { get; } = [];

    public Dictionary<string, LivemapTagRun> Runs { get; } = [];

    public Dictionary<long, string> AssetTexts { get; } = [];

    public Dictionary<string, List<LivemapProvenance>> Provenance { get; } = [];

    public List<long> Reruns { get; } = [];

    public List<(long AssetId, int MaxChars)> AssetReads { get; } = [];

    public List<string> Calls { get; } = [];

    public static string AssetName(string tag) => $"EnshroudedLiveMap-{tag[1..]}.zip";

    public static LivemapRelease ReleaseOf(string tag, bool draft = false, bool immutable = true, string? digest = null) => new(
        tag,
        draft,
        false,
        immutable,
        $"https://github.example/{Fork}/releases/tag/{tag}",
        [
            new LivemapReleaseAsset(ZipAssetId, AssetName(tag), 9000, digest ?? $"sha256:{ZipSha}"),
            new LivemapReleaseAsset(ChecksumAssetId, AssetName(tag) + ".sha256", 94, null),
        ]);

    public static LivemapProvenance ProvenanceOf(string tag) => new(
        [new LivemapProvenanceSubject(AssetName(tag), ZipSha)],
        ".github/workflows/release.yaml",
        $"refs/tags/{tag}",
        $"https://github.com/{Fork}",
        TagCommit);

    public void Seed(string tag, bool latest = true)
    {
        var release = ReleaseOf(tag);
        Releases[tag] = release;
        if (latest)
        {
            Latest = release;
        }

        TagCommits[tag] = TagCommit;
        AssetTexts[ChecksumAssetId] = $"{ZipSha}  {AssetName(tag)}\n";
        Provenance[ZipSha] = [ProvenanceOf(tag)];
    }

    public Task<LivemapRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        Calls.Add("latest");
        return Task.FromResult(Latest);
    }

    public Task<LivemapRelease?> GetByTagAsync(string tag, CancellationToken cancellationToken)
    {
        Calls.Add($"release {tag}");
        return Task.FromResult(Releases.GetValueOrDefault(tag));
    }

    public Task<string?> GetTagCommitAsync(string tag, CancellationToken cancellationToken)
    {
        Calls.Add($"commit {tag}");
        return Task.FromResult(TagCommits.GetValueOrDefault(tag));
    }

    public Task<LivemapTagRun?> GetTagRunAsync(string tag, CancellationToken cancellationToken)
    {
        Calls.Add($"run {tag}");
        return Task.FromResult(Runs.GetValueOrDefault(tag));
    }

    public Task RerunFailedJobsAsync(long runId, CancellationToken cancellationToken)
    {
        Reruns.Add(runId);
        return Task.CompletedTask;
    }

    public Task<string?> ReadAssetTextAsync(long assetId, int maxChars, CancellationToken cancellationToken)
    {
        AssetReads.Add((assetId, maxChars));
        return Task.FromResult(AssetTexts.GetValueOrDefault(assetId));
    }

    public Task<IReadOnlyList<LivemapProvenance>> GetProvenanceAsync(string sha256, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LivemapProvenance>>(Provenance.GetValueOrDefault(sha256) ?? []);
}
