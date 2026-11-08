namespace AgentPlatform.Host.Livemap;

public enum LivemapWorkflowFile
{
    ExeCapture,
    Compat,
}

public sealed record LivemapRun(long Id, string Status, string? Conclusion, string HeadSha)
{
    public bool IsCompleted => Status == "completed";

    public bool Succeeded => IsCompleted && Conclusion == "success";
}

public interface ILivemapGitHub
{
    Task<long> DispatchAsync(LivemapWorkflowFile workflow, long buildId, string manifestGid, CancellationToken cancellationToken);

    Task<LivemapRun?> GetRunAsync(long runId, CancellationToken cancellationToken);

    Task<CompatArtifact?> ReadCompatArtifactAsync(long runId, CancellationToken cancellationToken);

    Task<string?> ReadBuildsJsonAsync(string sha, CancellationToken cancellationToken);
}
