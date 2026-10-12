using System.Net;
using System.Text;
using AgentPlatform.Host.Gateway;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Tests.GitHub;

public sealed class GitHubWebhookEndpointTests : IAsyncDisposable
{
    private const string Path = "/webhooks/github";
    private const string BodyMarker = "body-marker-3f9a";

    private readonly RecordingSignaler _signaler = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public GitHubWebhookEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("GitHub:WebhookSecret", GitHubPayloads.Secret);
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRenovateTriageSignaler>(_signaler);
                services.AddSingleton<ILoggerProvider>(_logs);
            });
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingSignaler : IRenovateTriageSignaler
    {
        public List<CheckSuiteCompletedSignal> Signals { get; } = [];

        public Task SignalAsync(CheckSuiteCompletedSignal signal, CancellationToken cancellationToken)
        {
            Signals.Add(signal);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                var message = formatter(state, exception);
                Messages.Add(message);
                Entries.Add((logLevel, message));
            }
        }

        public void Dispose()
        {
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpResponseMessage> PostAsync(string body, string? eventName, string? signature, string? delivery = "delivery-1")
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddHeader(request, "X-GitHub-Event", eventName);
        AddHeader(request, "X-Hub-Signature-256", signature);
        AddHeader(request, "X-GitHub-Delivery", delivery);
        return await client.SendAsync(request, Ct);
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private Task<HttpResponseMessage> PostSignedAsync(string body, string eventName, string? delivery = "delivery-1") =>
        PostAsync(body, eventName, GitHubPayloads.Sign(body), delivery);

    [Fact]
    public async Task APing_Returns200AndSignalsNothing()
    {
        using var response = await PostSignedAsync("""{"zen":"Keep it simple."}""", "ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ACompletedRenovateCheckSuite_IsForwardedWithTheDeliveryId()
    {
        var delivery = Guid.NewGuid().ToString();

        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite", delivery);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var signal = Assert.Single(_signaler.Signals);
        Assert.Equal(GitHubPayloads.Repo, signal.Repo);
        Assert.Equal(7, signal.PrNumber);
        Assert.Equal(delivery, signal.DeliveryId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("forged INFO fake log line")]
    public async Task ADeliveryHeaderThatIsNotAGuid_IsForwardedWithAnEmptyDeliveryId(string delivery)
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite", delivery);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(_signaler.Signals).DeliveryId);
    }

    [Fact]
    public async Task ACheckSuiteFromAnAppOtherThanGitHubActions_IsForwarded()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(appSlug: "sonarqubecloud"), "check_suite");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("sonarqubecloud", Assert.Single(_signaler.Signals).AppSlug);
    }

    [Fact]
    public async Task ACheckSuiteWithoutADeliveryHeader_IsForwardedWithAnEmptyDeliveryId()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite", delivery: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(_signaler.Signals).DeliveryId);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("rerequested")]
    public async Task ACheckSuiteActionOtherThanCompleted_IsAcknowledgedAndDropped(string action)
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(action: action), "check_suite");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ACheckSuiteThatFailsAFilter_IsAcknowledgedAndDropped()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(branch: "feature/x"), "check_suite");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("check_run")]
    [InlineData("workflow_run")]
    [InlineData("installation")]
    public async Task AnyOtherEvent_IsAcknowledgedAndDropped(string eventName)
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), eventName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ASignedRequestWithoutAnEventHeader_IsAcknowledgedAndDropped()
    {
        using var response = await PostAsync(GitHubPayloads.CheckSuite(), null, GitHubPayloads.Sign(GitHubPayloads.CheckSuite()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ASignedCheckSuiteWithAMalformedBody_Returns400()
    {
        using var response = await PostSignedAsync("{not json", "check_suite");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task AMissingSignature_Returns401AndSignalsNothing()
    {
        using var response = await PostAsync(GitHubPayloads.CheckSuite(), "check_suite", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ASignatureMadeWithAnotherSecret_Returns401AndSignalsNothing()
    {
        var body = GitHubPayloads.CheckSuite();

        using var response = await PostAsync(body, "check_suite", GitHubPayloads.Sign(body, "another-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ABodyChangedAfterSigning_Returns401AndSignalsNothing()
    {
        var signature = GitHubPayloads.Sign(GitHubPayloads.CheckSuite(branch: "feature/x"));

        using var response = await PostAsync(GitHubPayloads.CheckSuite(branch: "renovate/foo"), "check_suite", signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task AnUnsignedMalformedBody_Returns401NotAParseError()
    {
        using var response = await PostAsync("{not json", "check_suite", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARejectedRequest_LogsNoBodyAndReturnsNone()
    {
        var body = GitHubPayloads.CheckSuite().Replace("renovate/foo", BodyMarker, StringComparison.Ordinal);

        using var response = await PostAsync(body, "check_suite", "sha256=" + new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
        lock (_logs.Messages)
        {
            Assert.DoesNotContain(_logs.Messages, message => message.Contains(BodyMarker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ARejectedDelivery_IsLoggedAtDebug()
    {
        var delivery = Guid.NewGuid().ToString();

        using var response = await PostAsync(GitHubPayloads.CheckSuite(), "check_suite", null, delivery);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        lock (_logs.Messages)
        {
            var entry = Assert.Single(_logs.Entries, e => e.Message.Contains(delivery, StringComparison.Ordinal));
            Assert.Equal(LogLevel.Debug, entry.Level);
        }
    }

    [Theory]
    [InlineData("forged INFO fake log line")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task ADeliveryIdThatIsNotAGuid_IsNotLogged(string delivery)
    {
        using var response = await PostAsync(GitHubPayloads.CheckSuite(), "check_suite", null, delivery);
        using var signed = await PostSignedAsync("{not json", "check_suite", delivery);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, signed.StatusCode);
        lock (_logs.Messages)
        {
            Assert.NotEmpty(_logs.Entries);
            Assert.DoesNotContain(_logs.Entries, e => delivery.Length > 0 && e.Message.Contains(delivery, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ABodyOverTheSizeLimit_Returns413BeforeAnythingIsParsed()
    {
        var body = new string(' ', GitHubWebhookEndpoint.MaxBodyBytes + 1);

        using var response = await PostAsync(body, "check_suite", GitHubPayloads.Sign(body));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ABodyOverTheSizeLimit_IsLoggedAtDebug()
    {
        var delivery = Guid.NewGuid().ToString();
        var body = new string(' ', GitHubWebhookEndpoint.MaxBodyBytes + 1);

        using var response = await PostAsync(body, "check_suite", GitHubPayloads.Sign(body), delivery);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        lock (_logs.Messages)
        {
            var entry = Assert.Single(_logs.Entries, e => e.Message.Contains(delivery, StringComparison.Ordinal));
            Assert.Equal(LogLevel.Debug, entry.Level);
        }
    }

    [Fact]
    public async Task OtherMethods_AreNotAllowed()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task OnTheGatewayPort_TheWebhookIsNotServed()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new LocalPortFilter(8081))));
        using var client = factory.CreateClient();
        var body = GitHubPayloads.CheckSuite();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "check_suite");
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", GitHubPayloads.Sign(body));

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_signaler.Signals);
    }

    private sealed class LocalPortFilter(int port) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.LocalPort = port;
                    return nextMiddleware(context);
                });
                next(app);
            };
    }
}
