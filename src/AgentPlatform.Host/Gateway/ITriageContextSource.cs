using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Host.Gateway;

public interface ITriageContextSource
{
    Task<TriageContext?> GetAsync(AgentRunIdentity run, CancellationToken cancellationToken);
}
