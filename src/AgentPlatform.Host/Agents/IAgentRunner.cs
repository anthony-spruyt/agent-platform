namespace AgentPlatform.Host.Agents;

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);

    Task CancelAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

public interface IRunSecretContributor
{
    Task<IReadOnlyDictionary<string, string>> GetAdditionalDataAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
