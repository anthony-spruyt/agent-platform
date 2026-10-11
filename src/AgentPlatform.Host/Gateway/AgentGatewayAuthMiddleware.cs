using AgentPlatform.Host.Agents;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AgentPlatform.Host.Gateway;

public sealed class AgentGatewayAuthMiddleware(RequestDelegate next, AgentSessionTokens tokens, IOptions<AgentGatewayOptions> options)
{
    public const string McpPath = "/mcp";
    public const string RunItemKey = "agent-platform.run";

    private const string BearerPrefix = "Bearer ";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var port = options.Value.Port;
        var onGatewayPort = port > 0 && context.Connection.LocalPort == port;
        var isMcp = context.Request.Path.StartsWithSegments(McpPath, StringComparison.OrdinalIgnoreCase);
        if (isMcp != onGatewayPort)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        if (!isMcp)
        {
            return next(context);
        }

        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : null;
        if (!tokens.TryValidate(token, out var run))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return Task.CompletedTask;
        }

        context.Items[RunItemKey] = run;
        return next(context);
    }
}
