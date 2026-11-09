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
    int Commits,
    string Title = "");

public sealed record LivemapPullRequestFile(string Filename, string Status, string? PreviousFilename, string? Patch = null);

public sealed record LivemapCommit(IReadOnlyList<string> Parents, IReadOnlyList<LivemapPullRequestFile> Files);

public sealed record LivemapCheckRun(string Name, string Status, string? Conclusion, string? AppSlug, long CheckSuiteId);

public enum LivemapMergeResult
{
    Merged,
    Refused,

    // GitHub answers 405 while it still works out whether the pull request can merge, so the merge is asked for again.
    NotMergeable,
}

public interface ILivemapPullRequests
{
    Task<string> GetMainHeadAsync(CancellationToken cancellationToken);

    Task CreateBranchAsync(string branch, string oid, CancellationToken cancellationToken);

    Task<string> CommitFileAsync(string branch, string expectedHeadOid, string headline, string path, string content, CancellationToken cancellationToken);

    Task<string> CommitFilesAsync(string branch, string expectedHeadOid, string headline, IReadOnlyList<LivemapFile> files, CancellationToken cancellationToken);

    Task<int> OpenAsync(string branch, string title, string body, CancellationToken cancellationToken);

    Task<LivemapPullRequest?> GetAsync(int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapPullRequest>> FindOpenByBranchAsync(string branch, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapPullRequestFile>> ListFilesAsync(int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetParentsAsync(string commitSha, CancellationToken cancellationToken);

    Task<LivemapCommit> GetCommitAsync(string commitSha, CancellationToken cancellationToken);

    Task<string?> ReadFileAsync(string commitSha, string path, CancellationToken cancellationToken);

    Task<string?> GetFileModeAsync(string commitSha, string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapCheckRun>> ListCheckRunsAsync(string sha, CancellationToken cancellationToken);

    Task<LivemapMergeResult> MergeAsync(int number, string sha, string title, CancellationToken cancellationToken);

    Task ClosePullRequestAsync(int number, CancellationToken cancellationToken);

    Task DeleteBranchAsync(string branch, CancellationToken cancellationToken);

    Task<string> GetAppLoginAsync(CancellationToken cancellationToken);
}

// The same client pointed at the pin repository: a separate type so the two can be injected side by side.
public interface ILivemapPinPullRequests : ILivemapPullRequests;
