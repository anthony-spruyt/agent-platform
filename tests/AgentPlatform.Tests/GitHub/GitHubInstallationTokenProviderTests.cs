using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.GitHub;

public sealed class GitHubInstallationTokenProviderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> ChecksRead = new() { ["checks"] = "read" };

    private readonly RSA _key = GitHubPayloads.NewKey();
    private readonly ManualTime _time = new(Start);
    private int _mints;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _key.Dispose();

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        var path = request.Uri.AbsolutePath;
        if (request.Method == HttpMethod.Get && path.EndsWith("/installation", StringComparison.Ordinal))
        {
            var repo = path["/repos/".Length..^"/installation".Length];
            return repo.StartsWith("missing/", StringComparison.Ordinal)
                ? StubHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)
                : StubHttpHandler.Json($$"""{"id":{{(repo.StartsWith("octo/", StringComparison.Ordinal) ? 555 : 777)}}}""");
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/app/installations/", StringComparison.Ordinal) && path.EndsWith("/access_tokens", StringComparison.Ordinal))
        {
            var n = Interlocked.Increment(ref _mints);
            var expires = _time.Now + TimeSpan.FromHours(1);
            return StubHttpHandler.Json($$"""{"token":"ghs_token_{{n}}","expires_at":"{{expires:yyyy-MM-ddTHH:mm:ssZ}}"}""", HttpStatusCode.Created);
        }

        return StubHttpHandler.Json("{}", HttpStatusCode.InternalServerError);
    }

    private (GitHubInstallationTokenProvider Provider, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHttpHandler(respond ?? Respond);
        var factory = new StubHttpClientFactory(handler, new Uri("https://api.github.com/"));
        var options = Options.Create(new GitHubAppOptions { AppId = "12345", PrivateKey = _key.ExportRSAPrivateKeyPem() });
        return (new GitHubInstallationTokenProvider(factory, options, _time), handler);
    }

    private static byte[] Base64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    private static JsonElement Decode(string segment) => JsonDocument.Parse(Base64Url(segment)).RootElement;

    [Fact]
    public async Task MintsATokenScopedToOneRepositoryAndTheRequestedPermissionsOnly()
    {
        var (provider, handler) = Create();

        var token = await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        Assert.Equal("ghs_token_1", token);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/repos/octo/widgets/installation", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/app/installations/555/access_tokens", handler.Requests[1].Uri.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal(["widgets"], body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        var permissions = body.RootElement.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?> { ["checks"] = "read" }, permissions);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task TheInstallationIsLookedUpPerRepository()
    {
        var (provider, handler) = Create();

        await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);
        await provider.GetTokenAsync("other/gadgets", ChecksRead, Ct);

        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/app/installations/555/access_tokens");
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/app/installations/777/access_tokens");
    }

    [Fact]
    public async Task BothAppRequestsAreAuthenticatedWithASignedAppJwt()
    {
        var (provider, handler) = Create();

        await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        foreach (var request in handler.Requests)
        {
            Assert.StartsWith("Bearer ", request.Authorization, StringComparison.Ordinal);
            var parts = request.Authorization!["Bearer ".Length..].Split('.');
            Assert.Equal(3, parts.Length);
            Assert.Equal("RS256", Decode(parts[0]).GetProperty("alg").GetString());
            var claims = Decode(parts[1]);
            Assert.Equal("12345", claims.GetProperty("iss").GetString());
            var iat = claims.GetProperty("iat").GetInt64();
            var exp = claims.GetProperty("exp").GetInt64();
            Assert.True(iat < Start.ToUnixTimeSeconds());
            Assert.True(exp > Start.ToUnixTimeSeconds());
            Assert.True(exp - iat <= 600);
            Assert.True(_key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }

    [Fact]
    public async Task ATokenIsCachedUntilFiveMinutesBeforeItExpires()
    {
        var (provider, handler) = Create();
        var first = await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        _time.Now = Start.AddMinutes(54);
        var second = await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ATokenWithinFiveMinutesOfExpiry_IsReplaced()
    {
        var (provider, handler) = Create();
        await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        _time.Now = Start.AddMinutes(55);
        var renewed = await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);

        Assert.Equal("ghs_token_2", renewed);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task TokensAreNotSharedAcrossRepositoriesOrPermissionSets()
    {
        var (provider, _) = Create();

        var a = await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct);
        var otherRepo = await provider.GetTokenAsync("octo/gadgets", ChecksRead, Ct);
        var otherPermissions = await provider.GetTokenAsync("octo/widgets", new Dictionary<string, string> { ["pull_requests"] = "read" }, Ct);
        var both = await provider.GetTokenAsync("octo/widgets", new Dictionary<string, string> { ["checks"] = "read", ["pull_requests"] = "read" }, Ct);

        Assert.Equal(4, new[] { a, otherRepo, otherPermissions, both }.Distinct().Count());
    }

    [Fact]
    public async Task ThePermissionOrderDoesNotChangeTheCacheEntry()
    {
        var (provider, handler) = Create();

        var a = await provider.GetTokenAsync("octo/widgets", new Dictionary<string, string> { ["checks"] = "read", ["pull_requests"] = "read" }, Ct);
        var b = await provider.GetTokenAsync("octo/widgets", new Dictionary<string, string> { ["pull_requests"] = "read", ["checks"] = "read" }, Ct);

        Assert.Equal(a, b);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentCallsForTheSameTokenMintItOnce()
    {
        var (provider, _) = Create();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetTokenAsync("octo/widgets", ChecksRead, Ct)));

        Assert.Single(tokens.Distinct());
        Assert.Equal(1, _mints);
    }

    [Fact]
    public async Task ARepositoryWithoutTheAppInstalled_FailsWithoutMintingAToken()
    {
        var (provider, handler) = Create();

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetTokenAsync("missing/repo", ChecksRead, Ct));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AFailedMint_IsNotCached()
    {
        var failing = true;
        var (provider, _) = Create(request =>
            failing && request.Method == HttpMethod.Post
                ? StubHttpHandler.Json("""{"message":"boom"}""", HttpStatusCode.BadGateway)
                : Respond(request));

        await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetTokenAsync("octo/widgets", ChecksRead, Ct));
        failing = false;

        Assert.Equal("ghs_token_1", await provider.GetTokenAsync("octo/widgets", ChecksRead, Ct));
    }

    [Theory]
    [InlineData("""{"id":0}""")]
    [InlineData("""{}""")]
    public async Task AnInstallationLookupWithoutAnId_Fails(string response)
    {
        var (provider, _) = Create(_ => StubHttpHandler.Json(response));

        await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetTokenAsync("octo/widgets", ChecksRead, Ct));
    }

    [Theory]
    [InlineData("""{"token":"","expires_at":"2026-10-11T13:00:00Z"}""")]
    [InlineData("""{"token":"ghs_x"}""")]
    [InlineData("""{"expires_at":"2026-10-11T13:00:00Z"}""")]
    public async Task AMintResponseWithoutATokenOrExpiry_Fails(string mint)
    {
        var (provider, _) = Create(request => request.Method == HttpMethod.Post ? StubHttpHandler.Json(mint, HttpStatusCode.Created) : Respond(request));

        await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetTokenAsync("octo/widgets", ChecksRead, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("a/b/c")]
    [InlineData("../x")]
    [InlineData("owner/na me")]
    [InlineData("/name")]
    public async Task AnInvalidRepositoryName_IsRejectedBeforeAnyRequest(string repo)
    {
        var (provider, handler) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetTokenAsync(repo, ChecksRead, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptyPermissions_AreRejected()
    {
        var (provider, handler) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetTokenAsync("octo/widgets", new Dictionary<string, string>(), Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ABadPrivateKey_FailsWhenTheProviderIsBuilt()
    {
        var options = Options.Create(new GitHubAppOptions { AppId = "1", PrivateKey = "not a pem" });

        Assert.ThrowsAny<Exception>(() => new GitHubInstallationTokenProvider(new StubHttpClientFactory(new StubHttpHandler(Respond), new Uri("https://api.github.com/")), options, _time));
    }
}
