using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.GitHub;

public sealed record HookDeliveryPage(IReadOnlyList<HookDelivery> Deliveries, string? NextCursor);

public interface IGitHubAppClient
{
    Task<HookDeliveryPage> ListHookDeliveriesAsync(string? cursor, CancellationToken cancellationToken);

    // False when GitHub refuses the attempt (422): the delivery is no longer redeliverable, so there is nothing left to do.
    Task<bool> RedeliverAsync(long deliveryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(string owner, CancellationToken cancellationToken);
}

// Authenticates as the App itself (JWT): the hook delivery and installation endpoints reject installation tokens.
public sealed partial class GitHubAppClient(HttpClient http, IGitHubAppJwtSource jwt) : IGitHubAppClient
{
    private const int PageSize = 100;

    private static readonly Dictionary<string, string> _pullRequestsRead = new() { ["pull_requests"] = "read" };

    private sealed record DeliveryDto(long Id, string? Guid, DateTimeOffset DeliveredAt, bool Redelivery, int StatusCode, string? Event);

    private sealed record InstallationDto(long Id, AccountDto? Account);

    private sealed record AccountDto(string? Login);

    private sealed record AccessTokenDto(string? Token);

    private sealed record RepositoriesDto(List<RepositoryDto>? Repositories);

    private sealed record RepositoryDto(string? FullName);

    public async Task<HookDeliveryPage> ListHookDeliveriesAsync(string? cursor, CancellationToken cancellationToken)
    {
        var path = cursor is null
            ? $"app/hook/deliveries?per_page={PageSize}"
            : string.Create(CultureInfo.InvariantCulture, $"app/hook/deliveries?per_page={PageSize}&cursor={Uri.EscapeDataString(cursor)}");
        var (items, headers) = await GitHubHttp.SendWithHeadersAsync<List<DeliveryDto>>(
            http,
            GitHubHttp.Request(HttpMethod.Get, path, jwt.CreateJwt()),
            "hook deliveries list",
            cancellationToken).ConfigureAwait(false);

        return new HookDeliveryPage(
            [.. items.Select(d => new HookDelivery(d.Id, d.Guid ?? string.Empty, d.DeliveredAt, d.Redelivery, d.StatusCode, d.Event ?? string.Empty))],
            NextCursor(headers));
    }

    public async Task<bool> RedeliverAsync(long deliveryId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryId);

        using var request = GitHubHttp.Request(HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"app/hook/deliveries/{deliveryId}/attempts"), jwt.CreateJwt());
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return false;
        }

        GitHubHttp.EnsureSuccess(response, "hook delivery redelivery");
        return true;
    }

    public async Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var installations = await GitHubHttp.SendAsync<List<InstallationDto>>(
            http,
            GitHubHttp.Request(HttpMethod.Get, "app/installations?per_page=100", jwt.CreateJwt()),
            "installations list",
            cancellationToken).ConfigureAwait(false);
        var installation = installations.FirstOrDefault(i => string.Equals(i.Account?.Login, owner, StringComparison.OrdinalIgnoreCase));
        if (installation is null)
        {
            return [];
        }

        var token = await GitHubHttp.SendAsync<AccessTokenDto>(
            http,
            GitHubHttp.Request(
                HttpMethod.Post,
                string.Create(CultureInfo.InvariantCulture, $"app/installations/{installation.Id}/access_tokens"),
                jwt.CreateJwt(),
                JsonContent.Create(new { permissions = _pullRequestsRead }, options: GitHubJson.Options)),
            "installation token request",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.Token))
        {
            throw new GitHubApiException("GitHub installation token response lacked a token.");
        }

        var repos = new List<string>();
        for (var page = 1; page <= GitHubApiClient.MaxPages; page++)
        {
            var batch = (await GitHubHttp.SendAsync<RepositoriesDto>(
                http,
                GitHubHttp.Request(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"installation/repositories?per_page={PageSize}&page={page}"), token.Token),
                "installation repositories list",
                cancellationToken).ConfigureAwait(false)).Repositories ?? [];
            repos.AddRange(batch.Select(r => r.FullName).OfType<string>());
            if (batch.Count < PageSize)
            {
                return repos;
            }
        }

        throw new GitHubApiException($"GitHub installation repositories list exceeded {GitHubApiClient.MaxPages} pages.");
    }

    private static string? NextCursor(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        var next = links.SelectMany(l => l.Split(',')).FirstOrDefault(l => l.Contains("rel=\"next\"", StringComparison.Ordinal));
        return next is null || CursorPattern().Match(next) is not { Success: true } match ? null : Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex("[?&]cursor=([^&>]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CursorPattern();
}
