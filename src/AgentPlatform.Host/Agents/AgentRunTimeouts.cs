namespace AgentPlatform.Host.Agents;

public static class AgentRunTimeouts
{
    public const int DefaultPodDeadlineSeconds = 1800;
    public const int SchedulingGraceSeconds = 600;
    public const int CleanupSlackSeconds = 120;

    public static TimeSpan ActivityStartToClose(int podDeadlineSeconds) =>
        TimeSpan.FromSeconds(podDeadlineSeconds + SchedulingGraceSeconds + CleanupSlackSeconds);
}
