namespace AgentPlatform.Host.Livemap;

public sealed partial class LivemapBuildStore
{
    public async Task<bool> BeginPatchAsync(long buildId, long compatRunId, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Detected)
                {
                    return null;
                }

                build.Classification = LivemapClassification.Hard;
                build.CompatRunId = compatRunId;
                return LivemapBuildState.Patching;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    // The run number makes the count idempotent: an activity that is retried after a lost answer asks for the run it already has.
    public async Task<bool> BeginAgentRunAsync(long buildId, int run, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Patching || run < 1 || run < build.AgentRuns || run > build.AgentRuns + 1)
                {
                    return null;
                }

                build.AgentRuns = run;
                return LivemapBuildState.Patching;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> SavePartialAsync(long buildId, string notes, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Patching)
                {
                    return null;
                }

                build.PatchNotes = notes.Length > LivemapResult.MaxNotesLength ? notes[..LivemapResult.MaxNotesLength] : notes;
                return LivemapBuildState.Patching;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> StorePatchAsync(long buildId, string resultJson, string baseSha, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Patching)
                {
                    return null;
                }

                build.PatchResult = resultJson;
                build.PatchBaseSha = baseSha;
                build.PatchNotes = null;
                return LivemapBuildState.Classified;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    // A run that hit the usage limit did no work, so the caller may give its run back; no attempt is counted either way.
    public async Task<bool> ParkUntilAsync(long buildId, string detail, DateTimeOffset until, bool refundRun, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Patching)
                {
                    return null;
                }

                if (refundRun && build.AgentRuns > 0)
                {
                    build.AgentRuns--;
                }

                build.Detail = Truncate(detail);
                build.NextRetryAt = until;
                return LivemapBuildState.Parked;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> NeedsPersonAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Patching)
                {
                    return null;
                }

                build.Detail = Truncate(detail);
                return LivemapBuildState.NeedsOwner;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    // A code pull request is never merged by the worker: it stays open for the owner, and only a hard build can end this way.
    public async Task<bool> OpenOwnerReviewAsync(long buildId, int number, string headSha, CancellationToken cancellationToken) =>
        await MoveAsync(
            buildId,
            (build, _) =>
            {
                if (build.State != LivemapBuildState.Classified || build.Classification != LivemapClassification.Hard)
                {
                    return null;
                }

                build.PullRequestNumber = number;
                build.HeadSha = headSha;
                return LivemapBuildState.OwnerReview;
            },
            cancellationToken).ConfigureAwait(false) is not null;

    private static void ClearPatch(LivemapBuild build)
    {
        build.PatchResult = null;
        build.PatchBaseSha = null;
        build.PatchNotes = null;
    }
}
