using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

// A notice is found again by its marker, and only the app's own comment counts, so a retry never posts it twice.
public sealed class LivemapNotices(HttpClient http, IGitHubInstallationTokenProvider tokens, IOptions<LivemapOptions> options) : ILivemapNotices
{
    private const int PageSize = 100;

    private const int MaxPages = 10;

    private static readonly Dictionary<string, string> _issuesWrite = new() { ["issues"] = "write" };

    public async Task<bool> PostOnceAsync(long buildId, string appLogin, DateTimeOffset since, string body, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var livemap = options.Value;
        var (owner, name) = GitHubRepoName.Parse(livemap.Repo, nameof(LivemapOptions.Repo));
        var issue = livemap.TrackingIssue.ToString(CultureInfo.InvariantCulture);
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _issuesWrite, cancellationToken).ConfigureAwait(false);
        var marker = LivemapNotice.Marker(buildId);

        if (await HasNoticeAsync(owner, name, issue, token, marker, appLogin, since, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var payload = new JsonObject { ["body"] = $"{marker}\n{body}" };
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/issues/{issue}/comments", token, JsonContent.Create(payload)),
            "notice post",
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HasNoticeAsync(string owner, string name, string issue, string token, string marker, string appLogin, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var from = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        for (var page = 1; page <= MaxPages; page++)
        {
            var comments = await GitHubHttp.SendAsync<List<CommentDto>>(
                http,
                GitHubHttp.Request(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"repos/{owner}/{name}/issues/{issue}/comments?since={from}&per_page={PageSize}&page={page}"), token),
                "notice search",
                cancellationToken).ConfigureAwait(false);
            if (comments.Any(comment => comment.Body?.StartsWith(marker, StringComparison.Ordinal) == true && string.Equals(comment.User?.Login, appLogin, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (comments.Count < PageSize)
            {
                return false;
            }
        }

        throw new GitHubApiException("GitHub notice search returned more pages than the cap.", HttpStatusCode.OK);
    }

    private sealed record CommentDto(string? Body, UserDto? User);

    private sealed record UserDto(string? Login);
}
