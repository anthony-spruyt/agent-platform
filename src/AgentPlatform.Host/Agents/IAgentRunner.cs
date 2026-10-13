namespace AgentPlatform.Host.Agents;

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);

    Task CancelAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

public sealed record RunSecretContribution(IReadOnlyDictionary<string, string> Data, DateTimeOffset? ExpiresAt = null);

public interface IRunSecretContributor
{
    Task<RunSecretContribution> ContributeAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
