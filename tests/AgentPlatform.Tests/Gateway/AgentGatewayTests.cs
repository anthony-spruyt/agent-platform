using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace AgentPlatform.Tests.Gateway;

public sealed class AgentGatewayTests : IAsyncDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const int GatewayPort = 8081;
    private const int WebhookPort = 8080;
    private const string McpUrl = "http://localhost/mcp";

    private static readonly AgentSessionTokens Tokens = new(Key);
    private static readonly DateTimeOffset InAnHour = DateTimeOffset.UtcNow.AddHours(1);

    private readonly RecordingSubmitter _submitter = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AgentGatewayTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", Key);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IWorkflowResultSubmitter>(_submitter);
                services.AddSingleton<IStartupFilter>(new LocalPortFilter(GatewayPort, WebhookPort));
            });
        });
    }

    private sealed class LocalPortFilter(int gatewayPort, int webhookPort) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.LocalPort = context.Request.Path.StartsWithSegments(AgentGatewayAuthMiddleware.McpPath) ? gatewayPort : webhookPort;
                    return nextMiddleware(context);
                });
                next(app);
            };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingSubmitter : IWorkflowResultSubmitter
    {
        public List<(AgentRunIdentity Run, SubmitResultPayload Payload)> Signals { get; } = [];

        public Exception? Failure { get; set; }

        public Task SubmitResultAsync(AgentRunIdentity run, SubmitResultPayload payload, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Signals.Add((run, payload));
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<McpClient> ConnectAsync(string? token)
    {
        var options = new HttpClientTransportOptions { Endpoint = new Uri(McpUrl), TransportMode = HttpTransportMode.StreamableHttp };
        if (token is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        }

        var transport = new HttpClientTransport(options, _factory.CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string ValidToken() => Tokens.Issue("spike-1", "run-1", InAnHour);

    private async Task<HttpResponseMessage> PostRawAsync(string? authorization, string body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await client.SendAsync(request, Ct);
    }

    private const string CallBody = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"submit_result","arguments":{"status":"success","summary":"done"}}}""";

    [Fact]
    public async Task ValidToken_SignalsTheWaitingWorkflowWithTheStructuredPayload()
    {
        await using var client = await ConnectAsync(ValidToken());

        var result = await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?>
            {
                ["status"] = "success",
                ["summary"] = "Looks fine",
                ["data"] = JsonDocument.Parse("""{"verdict":"safe","confidence":0.9}""").RootElement,
            },
            cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var (run, payload) = Assert.Single(_submitter.Signals);
        Assert.Equal(new AgentRunIdentity("spike-1", "run-1"), run);
        Assert.Equal("success", payload.Status);
        Assert.Equal("Looks fine", payload.Summary);
        Assert.Equal("safe", payload.Data!.Value.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task ValidToken_WithoutOptionalData_StillSignals()
    {
        await using var client = await ConnectAsync(ValidToken());

        await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?> { ["status"] = "failure", ["summary"] = "could not check" },
            cancellationToken: Ct);

        var (_, payload) = Assert.Single(_submitter.Signals);
        Assert.Null(payload.Data);
    }

    [Fact]
    public async Task ExposesExactlyTheSubmitResultTool()
    {
        await using var client = await ConnectAsync(ValidToken());

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal("submit_result", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task IsStatelessSoACallNeedsNoSession()
    {
        using var response = await PostRawAsync($"Bearer {ValidToken()}", CallBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Single(_submitter.Signals);
    }

    [Fact]
    public async Task BadToken_IsRejectedAndNothingIsSignalled()
    {
        using var response = await PostRawAsync("Bearer v1.bm9wZQ.bm9wZQ.AAAA", CallBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_IsRejected()
    {
        var foreign = new AgentSessionTokens("another-key-another-key-another-key").Issue("spike-1", "run-1", InAnHour);

        using var response = await PostRawAsync($"Bearer {foreign}", CallBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var expired = Tokens.Issue("spike-1", "run-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await PostRawAsync($"Bearer {expired}", CallBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_submitter.Signals);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task MissingOrMalformedAuthorization_IsRejected(string? authorization)
    {
        using var response = await PostRawAsync(authorization, CallBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task ClientConnectWithBadToken_Fails()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("v1.bm9wZQ.bm9wZQ.AAAA"));
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task WhenTheRunIsNoLongerActive_TheAgentGetsAToolError()
    {
        _submitter.Failure = new RunNotActiveException("spike-1");
        await using var client = await ConnectAsync(ValidToken());

        var result = await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?> { ["status"] = "success", ["summary"] = "done" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("no longer", string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenAResultWasAlreadySubmitted_TheAgentGetsAnAlreadySubmittedToolError()
    {
        _submitter.Failure = new ResultAlreadySubmittedException("spike-1");
        await using var client = await ConnectAsync(ValidToken());

        var result = await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?> { ["status"] = "success", ["summary"] = "done" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("already", string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "summary")]
    [InlineData("success", "")]
    [InlineData("success", "   ")]
    [InlineData("done", "summary")]
    [InlineData("SUCCESS", "summary")]
    [InlineData("ok", "summary")]
    public async Task InvalidPayload_IsAToolErrorAndNothingIsSignalled(string status, string summary)
    {
        await using var client = await ConnectAsync(ValidToken());

        var result = await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?> { ["status"] = status, ["summary"] = summary },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task OversizedSummary_IsAToolError()
    {
        await using var client = await ConnectAsync(ValidToken());

        var result = await client.CallToolAsync(
            "submit_result",
            new Dictionary<string, object?> { ["status"] = "success", ["summary"] = new string('x', SubmitResultTool.MaxSummaryLength + 1) },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Empty(_submitter.Signals);
    }

    [Fact]
    public async Task HealthEndpointsStayOpenWithoutAToken()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(8080, false)]
    [InlineData(8081, true)]
    public async Task Middleware_ServesMcpOnlyOnTheGatewayPort(int localPort, bool served)
    {
        var middleware = new AgentGatewayAuthMiddleware(
            _ => Task.CompletedTask,
            Tokens,
            Microsoft.Extensions.Options.Options.Create(new AgentGatewayOptions { SessionTokenKey = Key, Port = 8081 }));
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Connection.LocalPort = localPort;
        context.Request.Headers.Authorization = $"Bearer {ValidToken()}";

        await middleware.InvokeAsync(context);

        Assert.Equal(served ? StatusCodes.Status200OK : StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/mcpx")]
    [InlineData("/other/mcp")]
    public async Task Middleware_OnTheGatewayPort_ServesNothingButMcp(string path)
    {
        var called = false;
        var middleware = new AgentGatewayAuthMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Tokens,
            Microsoft.Extensions.Options.Options.Create(new AgentGatewayOptions { SessionTokenKey = Key, Port = 8081 }));
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.LocalPort = 8081;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task Middleware_WithNoGatewayPortConfigured_ServesNothingOnMcp()
    {
        var called = false;
        var middleware = new AgentGatewayAuthMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Tokens,
            Microsoft.Extensions.Options.Options.Create(new AgentGatewayOptions { SessionTokenKey = Key, Port = 0 }));
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Connection.LocalPort = 8081;
        context.Request.Headers.Authorization = $"Bearer {ValidToken()}";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task Middleware_LeavesOtherPathsAlone()
    {
        var called = false;
        var middleware = new AgentGatewayAuthMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Tokens,
            Microsoft.Extensions.Options.Options.Create(new AgentGatewayOptions { SessionTokenKey = Key, Port = 8081 }));
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";
        context.Connection.LocalPort = 8080;

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }
}
