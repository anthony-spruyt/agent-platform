namespace AgentPlatform.Host.Agents;

public static class AgentRunTimeouts
{
    public const int DefaultPodDeadlineSeconds = 1800;
    public const int SchedulingGraceSeconds = 600;
    public const int CleanupSlackSeconds = 120;
    public const int TokenGraceSeconds = 60;

    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan ActivityScheduleToClose = TimeSpan.FromMinutes(110);

    public static TimeSpan ActivityStartToClose(int podDeadlineSeconds) =>
        TimeSpan.FromSeconds(podDeadlineSeconds + SchedulingGraceSeconds + CleanupSlackSeconds);
}
