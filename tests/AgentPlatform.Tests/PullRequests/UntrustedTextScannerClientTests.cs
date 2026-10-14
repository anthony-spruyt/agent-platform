using System.Net;
using System.Text.Json;
using AgentPlatform.Host.PullRequests;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.PullRequests;

public class UntrustedTextScannerClientTests
{
    private const string GoldenText = "héllo ✓";
    private const string GoldenHash = "sha256:5657cdef8a85a584e0e961e6f8247cf5d3f8ed21496ed6fdbcfd43a761e94245";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UntrustedTextScannerClient Create(Func<RecordedRequest, HttpResponseMessage> respond, out StubHttpHandler handler, Action<UntrustedTextScannerOptions>? configure = null, TimeProvider? time = null)
    {
        handler = new StubHttpHandler(respond);
        var options = new UntrustedTextScannerOptions
        {
            BaseUrl = "http://scanner.example.test:8080",
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };
        configure?.Invoke(options);
        return new UntrustedTextScannerClient(new StubHttpClientFactory(handler, new Uri("http://unused.example.test/")), Options.Create(options), time ?? TimeProvider.System);
    }

    private static HttpResponseMessage Flagged(params string[] hashes) =>
        StubHttpHandler.Json(JsonSerializer.Serialize(new { flagged = hashes, unknown = Array.Empty<string>() }));

    [Fact]
    public void TheHashIsTheLowercaseSha256OfTheUtf8Text()
    {
        Assert.Equal(GoldenHash, ScannedText.Create(GoldenText).Hash);
    }

    [Fact]
    public void ATextWhoseHashMatches_IsIntact_AndOneThatWasChangedIsNot()
    {
        var item = ScannedText.Create("original");

        Assert.True(item.IsIntact);
        Assert.False((item with { Text = "changed" }).IsIntact);
        Assert.False((item with { Hash = GoldenHash }).IsIntact);
    }

    [Fact]
    public async Task ACleanText_PostsOneItemWithItsHashAndNoKnownHashes()
    {
        var client = Create(_ => Flagged(), out var handler);

        var scan = await client.ScanAsync(GoldenText, Ct);

        Assert.Equal(ScanOutcome.Clean, scan.Outcome);
        Assert.Equal(new ScannedText(GoldenHash, GoldenText), scan.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://scanner.example.test:8080/v1/scan", request.Uri.ToString());
        using var body = JsonDocument.Parse(request.Body!);
        var item = Assert.Single(body.RootElement.GetProperty("new").EnumerateArray());
        Assert.Equal(GoldenHash, item.GetProperty("hash").GetString());
        Assert.Equal(GoldenText, item.GetProperty("text").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("known").GetArrayLength());
    }

    [Fact]
    public async Task AFlaggedHash_IsFlagged()
    {
        var client = Create(_ => Flagged(GoldenHash), out _);

        Assert.Equal(ScanOutcome.Flagged, (await client.ScanAsync(GoldenText, Ct)).Outcome);
    }

    [Fact]
    public async Task AFlaggedListOfOtherHashes_LeavesTheTextClean()
    {
        var client = Create(_ => Flagged("sha256:" + new string('a', 64)), out _);

        Assert.Equal(ScanOutcome.Clean, (await client.ScanAsync(GoldenText, Ct)).Outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"flagged":"x"}""")]
    [InlineData("""{"flagged":[1]}""")]
    [InlineData("null")]
    public async Task AMalformedBody_IsUnchecked(string body)
    {
        var client = Create(_ => StubHttpHandler.Json(body), out _);

        Assert.Equal(ScanOutcome.Unchecked, (await client.ScanAsync(GoldenText, Ct)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherFailureStatus_IsUncheckedWithoutARetry(HttpStatusCode status)
    {
        var client = Create(_ => StubHttpHandler.Json("""{"detail":"x"}""", status), out var handler);

        Assert.Equal(ScanOutcome.Unchecked, (await client.ScanAsync(GoldenText, Ct)).Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A503ThenAnAnswer_RetriesAndUsesTheAnswer()
    {
        var calls = 0;
        var client = Create(_ => Interlocked.Increment(ref calls) < 3 ? StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : Flagged(GoldenHash), out var handler);

        var scan = await client.ScanAsync(GoldenText, Ct);

        Assert.Equal(ScanOutcome.Flagged, scan.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AConnectionErrorThenAnAnswer_Retries()
    {
        var calls = 0;
        var client = Create(
            _ => Interlocked.Increment(ref calls) == 1 ? throw new HttpRequestException("refused") : Flagged(),
            out var handler);

        Assert.Equal(ScanOutcome.Clean, (await client.ScanAsync(GoldenText, Ct)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A503ThatOutlastsTheRetryBudget_IsUnchecked()
    {
        var client = Create(
            _ => StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable),
            out var handler,
            o => o.RetryBudget = TimeSpan.FromSeconds(60),
            new SteppingTime());

        var scan = await client.ScanAsync(GoldenText, Ct);

        Assert.Equal(ScanOutcome.Unchecked, scan.Outcome);
        Assert.True(handler.Requests.Count > 1);
    }

    // Every clock read advances ten seconds and a retry pause ends at once, so the budget runs out after a fixed number of requests; the request timeout stays a real timer.
    private sealed class SteppingTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, 10 * TimeSpan.TicksPerSecond);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime <= TimeSpan.FromSeconds(1) ? TimeSpan.Zero : dueTime, period);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task ARequestPastTheTimeout_IsUncheckedWithoutARetry()
    {
        var handler = new HangingHandler();
        var options = new UntrustedTextScannerOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) };
        var client = new UntrustedTextScannerClient(new StubHttpClientFactory(handler, new Uri("http://unused.example.test/")), Options.Create(options), TimeProvider.System);

        var scan = await client.ScanAsync(GoldenText, Ct);

        Assert.Equal(ScanOutcome.Unchecked, scan.Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ACallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Create(_ => Flagged(), out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ScanAsync(GoldenText, cts.Token));
    }

    [Fact]
    public async Task ConcurrentCallers_AreScannedOneAfterAnother()
    {
        var inFlight = 0;
        var overlapped = false;
        var client = Create(
            _ =>
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    overlapped = true;
                }

                Thread.Sleep(20);
                Interlocked.Decrement(ref inFlight);
                return Flagged();
            },
            out var handler);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.ScanAsync($"text {i}", Ct)));

        Assert.False(overlapped);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public void TheScannerIsRegisteredAsASingletonWithItsDefaults()
    {
        var services = new ServiceCollection();
        services.AddPullRequests(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var scanner = provider.GetRequiredService<IUntrustedTextScanner>();

        Assert.Same(scanner, provider.GetRequiredService<IUntrustedTextScanner>());
        var options = provider.GetRequiredService<IOptions<UntrustedTextScannerOptions>>().Value;
        Assert.Equal("http://litellm-llm-tool-guard.litellm.svc:8080", options.BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(330), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.RetryBudget);
    }

    [Theory]
    [InlineData("BaseUrl", "not a url")]
    [InlineData("BaseUrl", "ftp://scanner")]
    [InlineData("RequestTimeout", "00:00:00")]
    [InlineData("RetryBudget", "-00:00:01")]
    [InlineData("RetryDelay", "00:00:00")]
    public void AnInvalidScannerSetting_FailsAtStartup(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"TextScanner:{key}"] = value }).Build();

        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPullRequests(configuration));

        Assert.Contains($"TextScanner:{key}", failure.Message, StringComparison.Ordinal);
    }
}
