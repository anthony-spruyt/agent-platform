using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapBuildOutcome
{
    Classified,
    NeedsOwner,
    Parked,
    Superseded,
    PullRequestWaiting,
    Merged,
    ReleaseWaiting,
    PinWaiting,
    Shipped,
}

public sealed record LivemapDispatchInput(LivemapWorkflowFile Workflow, long BuildId, string ManifestGid);

public sealed record LivemapClassifyInput(long BuildId, string ManifestGid, long CompatRunId);

public sealed record LivemapParkInput(long BuildId, string Detail);

public sealed class LivemapBuildActivities(ILivemapGitHub github, LivemapBuildStore builds)
{
    public const string InFlightName = "LivemapBuildInFlight";
    public const string DispatchName = "LivemapBuildDispatch";
    public const string GetRunName = "LivemapBuildGetRun";
    public const string ClassifyName = "LivemapBuildClassify";
    public const string ParkName = "LivemapBuildPark";

    public const int MaxAttempts = 3;

    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    [Activity(InFlightName)]
    public async Task<bool> IsInFlightAsync(long buildId) =>
        await builds.FindAsync(buildId, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false) is { State: LivemapBuildState.Detected };

    [Activity(DispatchName)]
    public Task<long> DispatchAsync(LivemapDispatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return github.DispatchAsync(input.Workflow, input.BuildId, input.ManifestGid, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity(GetRunName)]
    public Task<LivemapRun?> GetRunAsync(long runId) => github.GetRunAsync(runId, ActivityExecutionContext.Current.CancellationToken);

    [Activity(ClassifyName)]
    public async Task<LivemapBuildOutcome> ClassifyAsync(LivemapClassifyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var run = await github.GetRunAsync(input.CompatRunId, cancellationToken).ConfigureAwait(false);
        if (run is not { Succeeded: true })
        {
            return await ParkAsync(input.BuildId, run is null ? "The compat run could not be found." : $"The compat run {(run.IsCompleted ? $"ended with {run.Conclusion}" : $"is {run.Status}")}.", cancellationToken).ConfigureAwait(false);
        }

        CompatArtifact? artifact;
        string? fork;
        try
        {
            artifact = await github.ReadCompatArtifactAsync(input.CompatRunId, cancellationToken).ConfigureAwait(false);
            if (artifact is null)
            {
                return await ParkAsync(input.BuildId, "The compat run left no artifact.", cancellationToken).ConfigureAwait(false);
            }

            fork = await github.ReadBuildsJsonAsync(run.HeadSha, cancellationToken).ConfigureAwait(false);
        }
        catch (CompatArtifactException ex)
        {
            return await FlagAsync(input.BuildId, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        var outcome = CompatClassifier.Classify(artifact, fork, input.BuildId, input.ManifestGid);
        if (outcome.Kind is CompatOutcomeKind.Known or CompatOutcomeKind.Easy)
        {
            var classification = outcome.Kind == CompatOutcomeKind.Known ? LivemapClassification.Known : LivemapClassification.Easy;
            return await builds.ClassifyAsync(input.BuildId, classification, input.CompatRunId, cancellationToken).ConfigureAwait(false)
                ? LivemapBuildOutcome.Classified
                : LivemapBuildOutcome.Superseded;
        }

        return await FlagAsync(input.BuildId, outcome.Detail, cancellationToken).ConfigureAwait(false);
    }

    [Activity(ParkName)]
    public Task<LivemapBuildOutcome> ParkAsync(LivemapParkInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ParkAsync(input.BuildId, input.Detail, ActivityExecutionContext.Current.CancellationToken);
    }

    private async Task<LivemapBuildOutcome> FlagAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await builds.FlagForOwnerAsync(buildId, detail, cancellationToken).ConfigureAwait(false)
            ? LivemapBuildOutcome.NeedsOwner
            : LivemapBuildOutcome.Superseded;

    private async Task<LivemapBuildOutcome> ParkAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await builds.ParkAsync(buildId, detail, MaxAttempts, RetryAfter, cancellationToken).ConfigureAwait(false) switch
        {
            LivemapBuildState.Parked => LivemapBuildOutcome.Parked,
            LivemapBuildState.NeedsOwner => LivemapBuildOutcome.NeedsOwner,
            _ => LivemapBuildOutcome.Superseded,
        };
}
