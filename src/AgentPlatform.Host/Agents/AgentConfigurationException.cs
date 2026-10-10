namespace AgentPlatform.Host.Agents;

public sealed class AgentConfigurationException : InvalidOperationException
{
    public AgentConfigurationException(string message)
        : base(message)
    {
    }
}
