using System.ComponentModel;
using AgentPlatform.Host.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentPlatform.Host.Gateway;

[McpServerToolType]
public sealed class GetTriageContextTool(IHttpContextAccessor httpContextAccessor, ITriageContextSource source)
{
    [McpServerTool(Name = "get_triage_context", ReadOnly = true)]
    [Description("Returns one section of the pre-fetched triage context for your run: the facts and an index first, then the changelogs, CI logs and other text that was fetched for you. Start at section 1; the index says which section holds what.")]
    public async Task<string> GetTriageContextAsync(
        [Description("The section to read, starting at 1.")] int section = 1,
        CancellationToken cancellationToken = default)
    {
        var run = httpContextAccessor.HttpContext?.Items[AgentGatewayAuthMiddleware.RunItemKey] as AgentRunIdentity
            ?? throw new McpException("The request carries no valid run token.");

        try
        {
            var context = await source.GetAsync(run, cancellationToken).ConfigureAwait(false)
                ?? throw new McpException("This run has no triage context.");
            return section >= 1 && section <= context.Sections.Count
                ? context.Sections[section - 1]
                : throw new McpException($"section must be between 1 and {context.Sections.Count}.");
        }
        catch (RunNotActiveException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
