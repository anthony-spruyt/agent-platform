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
        var state = row.State == LivemapBuildState.Parked && await builds.ResumeAsync(row.BuildId, cancellationToken).ConfigureAwait(false)
            ? LivemapBuildState.Detected
            : row.State;
        if (state == LivemapBuildState.Detected)
        {
            await starter.StartAsync(row.BuildId, row.ManifestGid, cancellationToken).ConfigureAwait(false);
        }

        return new LivemapPollResult(outcome, current.BuildId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded Enshrouded server build {BuildId}; already covered by the pinned release: {Covered}.")]
    private static partial void LogRecorded(ILogger logger, long buildId, bool covered);
}
