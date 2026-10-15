using System.Net;
using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Workflows;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Tests.GitHub;

public sealed class GitHubWebhookBodyTimeoutTests : IAsyncDisposable
{
    private const string Path = "/webhooks/github";

    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private readonly BodyFailure _failure = new();
    private readonly WebApplicationFactory<Program> _factory;

    public GitHubWebhookBodyTimeoutTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("GitHub:WebhookSecret", GitHubPayloads.Secret);
            builder.UseSetting("GitHub:AppId", "3233443");
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRenovateTriageSignaler, NoSignaler>();
                services.AddSingleton<ILoggerProvider>(new CapturingProvider(_logs));
                services.AddSingleton(_failure);
                services.AddSingleton<IStartupFilter, SlowBodyFilter>();
            });
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private sealed class NoSignaler : IRenovateTriageSignaler
    {
        public Task SignalAsync(CheckSuiteCompletedSignal signal, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BodyFailure
    {
        public int StatusCode { get; set; } = Microsoft.AspNetCore.Http.StatusCodes.Status408RequestTimeout;
    }

    private sealed class SlowBodyFilter(BodyFailure failure) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Request.Body = new TimingOutStream(failure);
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class TimingOutStream(BodyFailure failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body failed.", failure.StatusCode);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new BadHttpRequestException("Request body failed.", failure.StatusCode);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new BadHttpRequestException("Request body failed.", failure.StatusCode);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingProvider(List<(LogLevel Level, string Message)> sink) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.GetType().Name)));
            }
        }

        public void Dispose()
        {
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string? eventName, string? delivery)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", GitHubPayloads.Sign("{}"));
        if (eventName is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Event", eventName);
        }

        if (delivery is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", delivery);
        }

        return await client.SendAsync(request, Ct);
    }

    private List<(LogLevel Level, string Message)> Snapshot()
    {
        lock (_logs)
        {
            return [.. _logs];
        }
    }

    [Fact]
    public async Task ABodyThatTimesOut_Gets408_AndOneWarningWithTheDeliveryIdAndEvent()
    {
        var delivery = Guid.NewGuid().ToString();

        using var response = await PostAsync("check_suite", delivery);

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        var warning = Assert.Single(Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains(delivery, StringComparison.Ordinal));
        Assert.Contains("check_suite", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Snapshot(), e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(Snapshot(), e => e.Message.Contains("BadHttpRequestException", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Check-Suiteé")]
    [InlineData("pull request")]
    [InlineData("a1234567890123456789012345678901234567890")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnEventNameThatIsNotPlain_IsLoggedAsUnknown(string? eventName)
    {
        var delivery = Guid.NewGuid().ToString();

        using var response = await PostAsync(eventName, delivery);

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        var warning = Assert.Single(Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains(delivery, StringComparison.Ordinal));
        Assert.Contains("unknown", warning.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(eventName))
        {
            Assert.DoesNotContain(eventName, warning.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADeliveryIdThatIsNotAGuid_IsLoggedAsUnknown()
    {
        using var response = await PostAsync("check_suite", "not-a-guid<script>");

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        var warning = Assert.Single(Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.DoesNotContain("not-a-guid", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task ARequestFailureThatIsNotATimeout_IsNotLoggedAsOne(int status)
    {
        _failure.StatusCode = status;
        var delivery = Guid.NewGuid().ToString();

        try
        {
            using var response = await PostAsync("check_suite", delivery);
            Assert.NotEqual(HttpStatusCode.RequestTimeout, response.StatusCode);
        }
        catch (BadHttpRequestException ex)
        {
            Assert.Equal(status, ex.StatusCode);
        }

        Assert.DoesNotContain(Snapshot(), e => e.Message.Contains("timed out", StringComparison.Ordinal));
    }
}
