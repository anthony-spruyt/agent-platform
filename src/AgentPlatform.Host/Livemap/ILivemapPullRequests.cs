namespace AgentPlatform.Host.Livemap;

public sealed record LivemapPullRequest(
    int Number,
    string State,
    bool Merged,
    bool Draft,
    string BaseRef,
    string BaseRepo,
    string HeadRef,
    string HeadRepo,
    string HeadSha,
    string AuthorLogin,
    string AuthorType,
    int Commits);

public sealed record LivemapPullRequestFile(string Filename, string Status, string? PreviousFilename);

public sealed record LivemapCheckRun(string Name, string Status, string? Conclusion, string? AppSlug);

public enum LivemapMergeResult
{
    Merged,
    Refused,
}

public interface ILivemapPullRequests
{
    Task<string> GetMainHeadAsync(CancellationToken cancellationToken);

    Task CreateBranchAsync(string branch, string oid, CancellationToken cancellationToken);

    Task<string> CommitFileAsync(string branch, string expectedHeadOid, string headline, string path, string content, CancellationToken cancellationToken);

    Task<int> OpenAsync(string branch, string title, string body, CancellationToken cancellationToken);

    Task<LivemapPullRequest?> GetAsync(int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapPullRequest>> FindOpenByBranchAsync(string branch, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapPullRequestFile>> ListFilesAsync(int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetParentsAsync(string commitSha, CancellationToken cancellationToken);

    Task<string?> GetFileModeAsync(string commitSha, string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapCheckRun>> ListCheckRunsAsync(string sha, CancellationToken cancellationToken);

    Task<LivemapMergeResult> MergeAsync(int number, string sha, string title, CancellationToken cancellationToken);

    Task ClosePullRequestAsync(int number, CancellationToken cancellationToken);

    Task DeleteBranchAsync(string branch, CancellationToken cancellationToken);

    Task<string> GetAppLoginAsync(CancellationToken cancellationToken);
}
