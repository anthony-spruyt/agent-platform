using Temporalio.Activities;

namespace AgentPlatform.Host.Workflows;

public class PingActivities
{
    [Activity]
    public string Ping(string name) => $"pong: {name}";
}
