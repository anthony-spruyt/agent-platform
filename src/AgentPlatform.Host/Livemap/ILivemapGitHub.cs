namespace AgentPlatform.Host.Livemap;

public enum LivemapWorkflowFile
{
    ExeCapture,
    Compat,
}

public sealed record LivemapRun(long Id, string Status, string? Conclusion, string HeadSha, long CheckSuiteId)
{
    public bool IsCompleted => Status == "completed";

    public bool Succeeded => IsCompleted && Conclusion == "success";
}

public sealed record LivemapSuiteRun(string Event, string? HeadBranch, long RepositoryId, long HeadRepositoryId, string? Path = null)
{
    public const string CiWorkflowPath = ".github/workflows/ci.yaml";

    public bool IsOwnBuildRun =>
        HeadRepositoryId > 0
        && HeadRepositoryId == RepositoryId
        && (Event == "workflow_dispatch" || (Event == "pull_request" && LivemapBuildBranch.IsMatch(HeadBranch)));

    public bool IsCiRunOf(string branch) =>
        HeadRepositoryId > 0
        && HeadRepositoryId == RepositoryId
        && Event == "pull_request"
        && HeadBranch == branch
        && Path == CiWorkflowPath;
}

public interface ILivemapGitHub
{
    Task<long> DispatchAsync(LivemapWorkflowFile workflow, long buildId, string manifestGid, CancellationToken cancellationToken);

    Task<LivemapRun?> GetRunAsync(long runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LivemapSuiteRun>> ListSuiteRunsAsync(long checkSuiteId, CancellationToken cancellationToken);

    Task<CompatArtifact?> ReadCompatArtifactAsync(long runId, CancellationToken cancellationToken);

    Task<string?> ReadBuildsJsonAsync(string sha, CancellationToken cancellationToken);

    Task<string?> ReadBuildsSchemaAsync(string sha, CancellationToken cancellationToken);
}
