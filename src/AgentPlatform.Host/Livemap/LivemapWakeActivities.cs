using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapWakeOutcome
{
    NoBuildInFlight,
    NotRunning,
    Woken,
    NoRunForSuite,
    NotBuildRun,
}

public sealed partial class LivemapWakeActivities(
    LivemapBuildStore builds,
    ILivemapBuildSignaler signaler,
    ILivemapGitHub github,
    ILogger<LivemapWakeActivities> logger)
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

        var runs = await github.ListSuiteRunsAsync(wake.SuiteId, cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            LogNoRun(logger, wake.SuiteId);
            return LivemapWakeOutcome.NoRunForSuite;
        }

        if (!runs.All(run => run.IsOwnBuildRun))
        {
            LogNotBuildRun(logger, wake.SuiteId);
            return LivemapWakeOutcome.NotBuildRun;
        }

        return await signaler.SignalAsync(build.BuildId, wake, cancellationToken).ConfigureAwait(false)
            ? LivemapWakeOutcome.Woken
            : LivemapWakeOutcome.NotRunning;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Did not wake the Livemap build: check suite {SuiteId} has no workflow run in the fork.")]
    private static partial void LogNoRun(ILogger logger, long suiteId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Did not wake the Livemap build: a run of check suite {SuiteId} is neither dispatched by the fork nor the build's own pull request.")]
    private static partial void LogNotBuildRun(ILogger logger, long suiteId);
}
