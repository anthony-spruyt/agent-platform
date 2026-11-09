using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapWakeOutcome
{
    NoBuildInFlight,
    NotRunning,
    Woken,
}

public sealed class LivemapWakeActivities(LivemapBuildStore builds, ILivemapBuildSignaler signaler)
{
    public const string WakeName = "LivemapWake";

    [Activity(WakeName)]
    public async Task<LivemapWakeOutcome> WakeAsync(LivemapBuildWake wake)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        if (await builds.FindActiveAsync(cancellationToken).ConfigureAwait(false) is not { } build)
        {
            return LivemapWakeOutcome.NoBuildInFlight;
        }

        return await signaler.SignalAsync(build.BuildId, wake, cancellationToken).ConfigureAwait(false)
            ? LivemapWakeOutcome.Woken
            : LivemapWakeOutcome.NotRunning;
    }
}
