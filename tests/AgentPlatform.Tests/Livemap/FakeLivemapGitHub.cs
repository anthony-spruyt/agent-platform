using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

internal sealed class FakeLivemapGitHub : ILivemapGitHub
{
    public const long ExeCaptureRunId = 9001;
    public const long CompatRunId = 9002;
    public const long ForkId = 500;

    private readonly Lock _gate = new();

    private readonly List<long> _suiteLookups = [];

    private readonly Dictionary<long, Queue<LivemapRun?>> _runs = [];

    private readonly Dictionary<long, int> _reads = [];

    private readonly List<(LivemapWorkflowFile Workflow, long BuildId, string ManifestGid)> _dispatches = [];

    public Dictionary<long, CompatArtifact?> Artifacts { get; } = [];

    public string? ForkBuilds { get; set; }

    public string? BuildsReadAt { get; private set; }

    public Exception? DispatchFailure { get; set; }

    public Exception? ForkBuildsFailure { get; set; }

    public Exception? ArtifactFailure { get; set; }

    public Task? ReadGate { get; set; }

    public IReadOnlyList<LivemapSuiteRun> SuiteRuns { get; set; } = [new LivemapSuiteRun("workflow_dispatch", "main", ForkId, ForkId)];

    public Exception? SuiteRunsFailure { get; set; }

    public int FailFirstSuiteLookups { get; set; }

    public IReadOnlyList<long> SuiteLookups
    {
        get
        {
            lock (_gate)
            {
                return [.. _suiteLookups];
            }
        }
    }

    public IReadOnlyList<(LivemapWorkflowFile Workflow, long BuildId, string ManifestGid)> Dispatches
    {
        get
        {
            lock (_gate)
            {
                return [.. _dispatches];
            }
        }
    }

    public int ReadsOf(long runId)
    {
        lock (_gate)
        {
            return _reads.GetValueOrDefault(runId);
        }
    }

    public void ScriptRun(long runId, params LivemapRun?[] reads)
    {
        lock (_gate)
        {
            _runs[runId] = new Queue<LivemapRun?>(reads);
        }
    }

    public Task<long> DispatchAsync(LivemapWorkflowFile workflow, long buildId, string manifestGid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _dispatches.Add((workflow, buildId, manifestGid));
        }

        return DispatchFailure is null
            ? Task.FromResult(workflow == LivemapWorkflowFile.ExeCapture ? ExeCaptureRunId : CompatRunId)
            : Task.FromException<long>(DispatchFailure);
    }

    public async Task<LivemapRun?> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        LivemapRun? run;
        lock (_gate)
        {
            _reads[runId] = _reads.GetValueOrDefault(runId) + 1;
            run = !_runs.TryGetValue(runId, out var reads) || reads.Count == 0
                ? null
                : reads.Count > 1 ? reads.Dequeue() : reads.Peek();
        }

        if (ReadGate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        return run;
    }

    public Task<IReadOnlyList<LivemapSuiteRun>> ListSuiteRunsAsync(long checkSuiteId, CancellationToken cancellationToken)
    {
        int lookups;
        lock (_gate)
        {
            _suiteLookups.Add(checkSuiteId);
            lookups = _suiteLookups.Count;
        }

        return SuiteRunsFailure is not null && (FailFirstSuiteLookups == 0 || lookups <= FailFirstSuiteLookups)
            ? Task.FromException<IReadOnlyList<LivemapSuiteRun>>(SuiteRunsFailure)
            : Task.FromResult(SuiteRuns);
    }

    public Task<CompatArtifact?> ReadCompatArtifactAsync(long runId, CancellationToken cancellationToken) =>
        ArtifactFailure is null ? Task.FromResult(Artifacts.GetValueOrDefault(runId)) : Task.FromException<CompatArtifact?>(ArtifactFailure);

    public Task<string?> ReadBuildsJsonAsync(string sha, CancellationToken cancellationToken)
    {
        BuildsReadAt = sha;
        return ForkBuildsFailure is null ? Task.FromResult(ForkBuilds) : Task.FromException<string?>(ForkBuildsFailure);
    }
}
