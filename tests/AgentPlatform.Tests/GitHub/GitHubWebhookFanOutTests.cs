using System.Net;
using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.GitHub;

public sealed class GitHubWebhookFanOutTests : IAsyncDisposable
{
    private const string Path = "/webhooks/github";

    private readonly RecordingSignaler _signaler = new();
    private readonly RecordingEventHandler _extra = new("check_suite", "issues");
    private readonly WebApplicationFactory<Program> _factory;

    public GitHubWebhookFanOutTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", new string('k', 32));
            builder.UseSetting("GitHub:WebhookSecret", GitHubPayloads.Secret);
            builder.UseSetting("GitHub:AppId", "3233443");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRenovateTriageSignaler>(_signaler);
                services.AddSingleton<IGitHubEventHandler>(_extra);
            });
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpResponseMessage> PostAsync(string body, string eventName, string? signature)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", eventName);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", "6b1d5e0e-3c1f-4c52-a0a5-0b8a63a3c111");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        }

        return await client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> PostSignedAsync(string body, string eventName) => PostAsync(body, eventName, GitHubPayloads.Sign(body));

    [Fact]
    public async Task AnEventAnotherHandlerAccepts_ReachesIt_AndAnswers202()
    {
        using var response = await PostSignedAsync("""{"zen":"x"}""", "issues");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var received = Assert.Single(_extra.Received);
        Assert.Equal("issues", received.Name);
        Assert.Equal("6b1d5e0e-3c1f-4c52-a0a5-0b8a63a3c111", received.DeliveryId);
        Assert.Equal("""{"zen":"x"}""", Encoding.UTF8.GetString(received.Body.Span));
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task ACheckSuite_ReachesTheTriageHandlerAndTheOtherHandler()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_signaler.Signals);
        Assert.Single(_extra.Received);
    }

    [Fact]
    public async Task ACheckSuiteTriageDrops_StillReachesTheOtherHandler_AndTheOtherHandlerDecidesTheAnswer()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(branch: "feature/x"), "check_suite");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(_signaler.Signals);
        Assert.Single(_extra.Received);
    }

    [Fact]
    public async Task AnEventNoHandlerAccepts_Answers200_AndReachesNoHandler()
    {
        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "push");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_extra.Received);
        Assert.Empty(_signaler.Signals);
    }

    [Fact]
    public async Task AHandlerThatReportsAMalformedBody_Answers400()
    {
        _extra.Outcome = GitHubEventOutcome.Malformed;

        using var response = await PostSignedAsync("{}", "issues");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AHandlerThatThrows_LeavesTheTriageSignalSent_AndTheDeliveryFailsWith500()
    {
        _extra.Failure = new InvalidOperationException("boom");

        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(_signaler.Signals);
        Assert.Single(_extra.Received);
    }

    [Fact]
    public async Task TheTriageHandlerThrowing_DoesNotStopTheOtherHandler_AndTheDeliveryFailsWith500()
    {
        _signaler.OnSignal = () => throw new InvalidOperationException("temporal is down");

        using var response = await PostSignedAsync(GitHubPayloads.CheckSuite(), "check_suite");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(_extra.Received);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task ARequestWithoutAValidSignature_Answers401_AndReachesNoHandler(string? signature)
    {
        using var response = await PostAsync(GitHubPayloads.CheckSuite(), "check_suite", signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_extra.Received);
        Assert.Empty(_signaler.Signals);
    }
}
