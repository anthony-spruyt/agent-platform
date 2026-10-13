using System.ComponentModel;
using System.Text.Json;
using AgentPlatform.Host.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentPlatform.Host.Gateway;

[McpServerToolType]
public sealed class SubmitResultTool(IHttpContextAccessor httpContextAccessor, IWorkflowResultSubmitter submitter)
{
    public const string StatusSuccess = "success";
    public const string StatusFailure = "failure";
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

        if (status?.Trim() is not (StatusSuccess or StatusFailure))
        {
            throw new McpException($"status must be \"{StatusSuccess}\" or \"{StatusFailure}\".");
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
            await submitter.SubmitResultAsync(run, new SubmitResultPayload(status.Trim(), summary, data, run.Step, run.Attempt), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RunNotActiveException or ResultAlreadySubmittedException or InvalidResultException)
        {
            throw new McpException(ex.Message, ex);
        }

        return "Result accepted.";
    }
}
