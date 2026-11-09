namespace AgentPlatform.Host.Livemap;

public sealed record LivemapReleaseAsset(long Id, string Name, long Size, string? Digest);

public sealed record LivemapRelease(string Tag, bool Draft, bool Prerelease, bool Immutable, string Url, IReadOnlyList<LivemapReleaseAsset> Assets);

public sealed record LivemapTagRun(long Id, string Status, string? Conclusion, int Attempt, string HeadSha, string Url);

public sealed record LivemapProvenanceSubject(string Name, string Sha256);

public sealed record LivemapProvenance(IReadOnlyList<LivemapProvenanceSubject> Subjects, string WorkflowPath, string WorkflowRef, string Repository, string? CommitSha);

public interface ILivemapReleases
{
    Task<LivemapRelease?> GetLatestAsync(CancellationToken cancellationToken);

    Task<LivemapRelease?> GetByTagAsync(string tag, CancellationToken cancellationToken);

    Task<string?> GetTagCommitAsync(string tag, CancellationToken cancellationToken);

    Task<LivemapTagRun?> GetTagRunAsync(string tag, CancellationToken cancellationToken);

    Task RerunFailedJobsAsync(long runId, CancellationToken cancellationToken);

    Task<string?> ReadAssetTextAsync(long assetId, int maxChars, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapProvenance>> GetProvenanceAsync(string sha256, CancellationToken cancellationToken);
}
