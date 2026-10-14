using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.GitHub;

public interface IGitHubInstallationTokenProvider
{
    Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken);
}

public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

public interface IGitHubTokenMinter
{
    Task<InstallationToken> MintTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken);
}

public interface IGitHubAppJwtSource
{
    string CreateJwt();
}

public sealed class GitHubInstallationTokenProvider : IGitHubInstallationTokenProvider, IGitHubTokenMinter, IGitHubAppJwtSource, IDisposable
{
    private static readonly TimeSpan _refreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _jwtBackdate = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _jwtLifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClients;
    private readonly string _appId;
    private readonly TimeProvider _time;
    private readonly RSA _key = RSA.Create();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, InstallationToken> _cache = [];

    public GitHubInstallationTokenProvider(IHttpClientFactory httpClients, IOptions<GitHubAppOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _httpClients = httpClients;
        _time = time;
        _appId = options.Value.AppId;
        try
        {
            _key.ImportFromPem(options.Value.PrivateKey);
        }
        catch
        {
            _key.Dispose();
            throw;
        }
    }

    private sealed record Installation(long Id);

    private sealed record AccessToken(string? Token, DateTimeOffset? ExpiresAt);

    public void Dispose()
    {
        _key.Dispose();
        _gate.Dispose();
    }

    public async Task<InstallationToken> MintTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        var sorted = SortPermissions(permissions);
        return await MintAsync(owner, name, sorted.ToDictionary(p => p.Key, p => p.Value), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        var sorted = SortPermissions(permissions);
        var cacheKey = $"{repo}|{string.Join(',', sorted.Select(p => $"{p.Key}={p.Value}"))}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt - _refreshMargin > _time.GetUtcNow())
            {
                return cached.Token;
            }

            var minted = await MintAsync(owner, name, sorted.ToDictionary(p => p.Key, p => p.Value), cancellationToken).ConfigureAwait(false);
            _cache[cacheKey] = minted;
            return minted.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<KeyValuePair<string, string>> SortPermissions(IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0)
        {
            throw new ArgumentException("At least one permission is required.", nameof(permissions));
        }

        return [.. permissions.OrderBy(p => p.Key, StringComparer.Ordinal)];
    }

    private async Task<InstallationToken> MintAsync(string owner, string name, Dictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        using var http = _httpClients.CreateClient(GitHubHttp.ClientName);

        var installation = await GitHubHttp.SendAsync<Installation>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/installation", CreateJwt()),
            "installation lookup",
            cancellationToken).ConfigureAwait(false);
        if (installation.Id <= 0)
        {
            throw new GitHubApiException("GitHub installation lookup returned no installation id.");
        }

        var token = await GitHubHttp.SendAsync<AccessToken>(
            http,
            GitHubHttp.Request(
                HttpMethod.Post,
                $"app/installations/{installation.Id}/access_tokens",
                CreateJwt(),
                JsonContent.Create(new { repositories = new[] { name }, permissions }, options: GitHubJson.Options)),
            "installation token request",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.Token) || token.ExpiresAt is not { } expiresAt)
        {
            throw new GitHubApiException("GitHub installation token response lacked a token or an expiry.");
        }

        return new InstallationToken(token.Token, expiresAt);
    }

    public string CreateJwt()
    {
        var now = _time.GetUtcNow();
        var header = Base64Url.EncodeToString("""{"alg":"RS256","typ":"JWT"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = (now - _jwtBackdate).ToUnixTimeSeconds(),
            exp = (now - _jwtBackdate + _jwtLifetime).ToUnixTimeSeconds(),
            iss = _appId,
        }));
        var signature = _key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64Url.EncodeToString(signature)}";
    }
}
