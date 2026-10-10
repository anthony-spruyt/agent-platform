using System.ComponentModel;
using System.Text.Json;
using AgentPlatform.Host.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentPlatform.Host.Gateway;

[McpServerToolType]
public sealed class SubmitResultTool(IHttpContextAccessor httpContextAccessor, IWorkflowResultSubmitter submitter)
{
    public const int MaxStatusLength = 32;
    public const int MaxSummaryLength = 16 * 1024;
    public const int MaxDataLength = 64 * 1024;

    [McpServerTool(Name = "submit_result")]
    [Description("Hands your final result back to the workflow that started you. Call it exactly once, when you are finished; a second call is rejected.")]
    public async Task<string> SubmitResultAsync(
        [Description("Either \"success\" or \"failure\".")] string status,
        [Description("A short human-readable summary of the outcome.")] string summary,
        [Description("Optional structured details as a JSON object.")] JsonElement? data = null,
        CancellationToken cancellationToken = default)
    {
        var run = httpContextAccessor.HttpContext?.Items[AgentGatewayAuthMiddleware.RunItemKey] as AgentRunIdentity
            ?? throw new McpException("The request carries no valid run token.");

        if (string.IsNullOrWhiteSpace(status) || status.Length > MaxStatusLength)
        {
            throw new McpException($"status is required and must be at most {MaxStatusLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(summary) || summary.Length > MaxSummaryLength)
        {
            throw new McpException($"summary is required and must be at most {MaxSummaryLength} characters.");
        }

        if (data is { } details && details.GetRawText().Length > MaxDataLength)
        {
            throw new McpException($"data must be at most {MaxDataLength} characters of JSON.");
        }

        try
        {
            await submitter.SubmitResultAsync(run, new SubmitResultPayload(status.Trim(), summary, data), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RunNotActiveException or ResultAlreadySubmittedException)
        {
            throw new McpException(ex.Message, ex);
        }

        return "Result accepted.";
    }
}
