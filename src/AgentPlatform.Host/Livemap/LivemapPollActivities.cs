using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapPollOutcome
{
    Disabled,
    Unchanged,
    Shipped,
    Detected,
}

public sealed record LivemapPollResult(LivemapPollOutcome Outcome, long? BuildId);

public sealed partial class LivemapPollActivities(
    IOptions<LivemapOptions> options,
    ISteamAppInfo steam,
    IShippedBuilds shipped,
    LivemapBuildStore builds,
    ILivemapBuildStarter starter,
    ILivemapPullRequests pullRequests,
    ILogger<LivemapPollActivities> logger)
{
    public const string PollName = "LivemapPoll";

    [Activity(PollName)]
    public async Task<LivemapPollResult> PollAsync()
    {
        if (!options.Value.Enabled)
        {
            return new LivemapPollResult(LivemapPollOutcome.Disabled, null);
        }

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var current = await steam.GetPublicBuildAsync(cancellationToken).ConfigureAwait(false);
        var row = await builds.FindAsync(current.BuildId, cancellationToken).ConfigureAwait(false);
        var outcome = LivemapPollOutcome.Unchanged;
        if (row is null)
        {
            var covered = (await shipped.ReadAsync(cancellationToken).ConfigureAwait(false)).Contains(current.BuildId);
            row = await builds.RecordAsync(current.BuildId, current.ManifestGid, current.UpdatedAt, covered ? LivemapBuildState.Done : LivemapBuildState.Detected, cancellationToken).ConfigureAwait(false);
            outcome = covered ? LivemapPollOutcome.Shipped : LivemapPollOutcome.Detected;
            LogRecorded(logger, current.BuildId, covered);
        }

        await builds.SupersedeOlderAsync(current.BuildId, cancellationToken).ConfigureAwait(false);
        var resumed = await ResumeAsync(row, cancellationToken).ConfigureAwait(false);
        if (resumed || row.State is LivemapBuildState.Detected or LivemapBuildState.Classified or LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging)
        {
            await starter.StartAsync(row.BuildId, row.ManifestGid, cancellationToken).ConfigureAwait(false);
        }

        await ReleaseReplacedPullRequestsAsync(cancellationToken).ConfigureAwait(false);
        return new LivemapPollResult(outcome, current.BuildId);
    }

    private Task<bool> ResumeAsync(LivemapBuild row, CancellationToken cancellationToken) => row.State switch
    {
        LivemapBuildState.Parked => builds.ResumeAsync(row.BuildId, cancellationToken),
        LivemapBuildState.PullRequestWaiting when options.Value.AutoMerge => builds.ResumeWaitingAsync(row.BuildId, cancellationToken),
        _ => Task.FromResult(false),
    };

    // Last, so a fork that is slow to answer never holds up the newest build; the next poll repeats what failed.
    private async Task ReleaseReplacedPullRequestsAsync(CancellationToken cancellationToken)
    {
        foreach (var replaced in await builds.FindSupersededWithPullRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            await pullRequests.ClosePullRequestAsync(replaced.PullRequestNumber!.Value, cancellationToken).ConfigureAwait(false);
            await pullRequests.DeleteBranchAsync(LivemapBuildBranch.For(replaced.BuildId), cancellationToken).ConfigureAwait(false);
            await builds.ReleasePullRequestAsync(replaced.BuildId, cancellationToken).ConfigureAwait(false);
            LogReleased(logger, replaced.PullRequestNumber.Value, replaced.BuildId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed pull request {Number} of the replaced Enshrouded server build {BuildId} and deleted its branch.")]
    private static partial void LogReleased(ILogger logger, int number, long buildId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded Enshrouded server build {BuildId}; already covered by the pinned release: {Covered}.")]
    private static partial void LogRecorded(ILogger logger, long buildId, bool covered);
}
