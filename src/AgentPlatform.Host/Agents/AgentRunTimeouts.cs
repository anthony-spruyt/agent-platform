namespace AgentPlatform.Host.Agents;

public static class AgentRunTimeouts
{
    public const int DefaultPodDeadlineSeconds = 1800;
    public const int SchedulingGraceSeconds = 600;
    public const int CleanupSlackSeconds = 120;

    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(60);

    public static TimeSpan HeartbeatTimeout { get; private set; } = DefaultHeartbeatTimeout;

    public static void ConfigureHeartbeatTimeout(TimeSpan timeout) => HeartbeatTimeout = timeout;

    public static TimeSpan ActivityStartToClose(int podDeadlineSeconds) =>
        TimeSpan.FromSeconds(podDeadlineSeconds + SchedulingGraceSeconds + CleanupSlackSeconds);
}
