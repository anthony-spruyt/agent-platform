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
        if (await builds.FindAsync(current.BuildId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new LivemapPollResult(LivemapPollOutcome.Unchanged, current.BuildId);
        }

        var covered = (await shipped.ReadAsync(cancellationToken).ConfigureAwait(false)).Contains(current.BuildId);
        await builds.RecordAsync(current.BuildId, current.ManifestGid, current.UpdatedAt, covered ? LivemapBuildState.Done : LivemapBuildState.Detected, cancellationToken).ConfigureAwait(false);
        LogRecorded(logger, current.BuildId, covered);
        return new LivemapPollResult(covered ? LivemapPollOutcome.Shipped : LivemapPollOutcome.Detected, current.BuildId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded Enshrouded server build {BuildId}; already covered by the pinned release: {Covered}.")]
    private static partial void LogRecorded(ILogger logger, long buildId, bool covered);
}
