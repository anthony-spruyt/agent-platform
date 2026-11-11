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
        var startsCurrent = resumed || row.State is LivemapBuildState.Detected or LivemapBuildState.Patching or LivemapBuildState.Classified or LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging;
        if (startsCurrent)
        {
            await starter.StartAsync(row.BuildId, row.ManifestGid, cancellationToken).ConfigureAwait(false);
        }

        await ResumeHeldShipStepsAsync(cancellationToken).ConfigureAwait(false);
        await StartUnfinishedAsync(startsCurrent ? current.BuildId : null, cancellationToken).ConfigureAwait(false);
        await ReleaseReplacedPullRequestsAsync(cancellationToken).ConfigureAwait(false);
        return new LivemapPollResult(outcome, current.BuildId);
    }

    private Task<bool> ResumeAsync(LivemapBuild row, CancellationToken cancellationToken) => row.State switch
    {
        LivemapBuildState.Parked => builds.ResumeAsync(row.BuildId, cancellationToken),
        LivemapBuildState.PullRequestWaiting when options.Value.AutoMerge => builds.ResumeWaitingAsync(row.BuildId, cancellationToken),
        _ => Task.FromResult(false),
    };

    // A held release or pin has no workflow, and a newer build never replaces it, so only the switch can wake it.
    private async Task ResumeHeldShipStepsAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AutoMerge)
        {
            return;
        }

        foreach (var held in await builds.FindShipWaitingAsync(cancellationToken).ConfigureAwait(false))
        {
            await builds.ResumeWaitingAsync(held.BuildId, cancellationToken).ConfigureAwait(false);
        }
    }

    // A merge, a ship step or a notice that was cut short has no other way to finish, and a newer build never replaces it.
    private async Task StartUnfinishedAsync(long? startedBuildId, CancellationToken cancellationToken)
    {
        await StartAllAsync(await builds.FindMergingAsync(cancellationToken).ConfigureAwait(false), startedBuildId, cancellationToken).ConfigureAwait(false);
        await StartAllAsync(await builds.FindShippingAsync(cancellationToken).ConfigureAwait(false), startedBuildId, cancellationToken).ConfigureAwait(false);
        await StartAllAsync(await builds.FindUnnotifiedAsync(cancellationToken).ConfigureAwait(false), startedBuildId, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartAllAsync(IReadOnlyList<LivemapBuild> unfinished, long? startedBuildId, CancellationToken cancellationToken)
    {
        foreach (var build in unfinished.Where(b => b.BuildId != startedBuildId))
        {
            await starter.StartAsync(build.BuildId, build.ManifestGid, cancellationToken).ConfigureAwait(false);
        }
    }

    // Last, so a fork that is slow to answer never holds up the newest build; the next poll repeats what failed.
    private async Task ReleaseReplacedPullRequestsAsync(CancellationToken cancellationToken)
    {
        foreach (var replaced in await builds.FindSupersededWithLeftoversAsync(cancellationToken).ConfigureAwait(false))
        {
            var branch = LivemapBuildBranch.For(replaced.BuildId);
            foreach (var number in await OpenPullRequestsAsync(replaced, branch, cancellationToken).ConfigureAwait(false))
            {
                await pullRequests.ClosePullRequestAsync(number, cancellationToken).ConfigureAwait(false);
                LogClosed(logger, number, replaced.BuildId);
            }

            await pullRequests.DeleteBranchAsync(branch, cancellationToken).ConfigureAwait(false);
            await builds.ReleasePullRequestAsync(replaced.BuildId, cancellationToken).ConfigureAwait(false);
        }
    }

    // A build replaced before it stored its pull request number may still own one, so its branch is asked.
    private async Task<IReadOnlyList<int>> OpenPullRequestsAsync(LivemapBuild replaced, string branch, CancellationToken cancellationToken)
    {
        if (replaced.PullRequestNumber is not { } number)
        {
            var found = await pullRequests.FindOpenByBranchAsync(branch, cancellationToken).ConfigureAwait(false);
            return [.. found.Select(pull => pull.Number)];
        }

        var pull = await pullRequests.GetAsync(number, cancellationToken).ConfigureAwait(false);
        return pull is { State: "open" } ? [number] : [];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed pull request {Number} of the replaced Enshrouded server build {BuildId}.")]
    private static partial void LogClosed(ILogger logger, int number, long buildId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded Enshrouded server build {BuildId}; already covered by the pinned release: {Covered}.")]
    private static partial void LogRecorded(ILogger logger, long buildId, bool covered);
}
