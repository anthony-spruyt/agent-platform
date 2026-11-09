using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPlatform.Host.GitHub;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Host.Livemap;

// Each call asks for the installation token it needs and no more; none of them ever asks for workflows.
public sealed partial class LivemapPullRequests(HttpClient http, IGitHubInstallationTokenProvider tokens, IGitHubAppJwtSource jwt, IOptions<LivemapOptions> options) : ILivemapPullRequests
{
    private const string MainBranch = "main";

    private const int PageSize = 100;

    private const int MaxCheckRunPages = 10;

    private const int MaxErrorChars = 200;

    private const string CommitMutation = "mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid } } }";

    private static readonly Dictionary<string, string> _contentsRead = new() { ["contents"] = "read" };
    private static readonly Dictionary<string, string> _contentsWrite = new() { ["contents"] = "write" };
    private static readonly Dictionary<string, string> _pullRequestsRead = new() { ["pull_requests"] = "read" };
    private static readonly Dictionary<string, string> _pullRequestsWrite = new() { ["pull_requests"] = "write" };
    private static readonly Dictionary<string, string> _contentsAndPullRequestsWrite = new() { ["contents"] = "write", ["pull_requests"] = "write" };
    private static readonly Dictionary<string, string> _checksRead = new() { ["checks"] = "read" };

    public async Task<string> GetMainHeadAsync(CancellationToken cancellationToken)
    {
        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var reference = await GitHubHttp.SendAsync<RefDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/git/ref/heads/{MainBranch}", token),
            "main ref read",
            cancellationToken).ConfigureAwait(false);
        return reference.Object?.Sha is { } sha && LivemapCommitSha.IsMatch(sha)
            ? sha
            : throw new GitHubApiException("GitHub main ref read returned no commit sha.", HttpStatusCode.OK);
    }

    public async Task CreateBranchAsync(string branch, string oid, CancellationToken cancellationToken)
    {
        RequireBranch(branch);
        RequireSha(oid, nameof(oid));

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsWrite, cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["ref"] = $"refs/heads/{branch}", ["sha"] = oid };
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/git/refs", token, Json(body)),
            "branch create",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CommitFileAsync(string branch, string expectedHeadOid, string headline, string path, string content, CancellationToken cancellationToken)
    {
        RequireBranch(branch);
        RequireSha(expectedHeadOid, nameof(expectedHeadOid));
        ArgumentException.ThrowIfNullOrWhiteSpace(headline);
        ArgumentNullException.ThrowIfNull(content);
        RequirePath(path);

        var (owner, name) = Fork();
        var input = new JsonObject
        {
            ["branch"] = new JsonObject { ["repositoryNameWithOwner"] = $"{owner}/{name}", ["branchName"] = branch },
            ["message"] = new JsonObject { ["headline"] = headline },
            ["fileChanges"] = new JsonObject
            {
                ["additions"] = new JsonArray(new JsonObject { ["path"] = path, ["contents"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) }),
            },
            ["expectedHeadOid"] = expectedHeadOid,
        };
        var body = new JsonObject { ["query"] = CommitMutation, ["variables"] = new JsonObject { ["input"] = input } };
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsWrite, cancellationToken).ConfigureAwait(false);
        var answer = await GitHubHttp.SendAsync<JsonNode>(
            http,
            GitHubHttp.Request(HttpMethod.Post, "graphql", token, Json(body)),
            "commit",
            cancellationToken).ConfigureAwait(false);

        if (answer is not JsonObject root)
        {
            throw new GitHubApiException("GitHub commit returned an unreadable body.", HttpStatusCode.OK);
        }

        if (root["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = (string?)errors[0]?["message"] ?? "no message";
            throw new GitHubApiException($"GitHub commit was refused: {Truncate(message)}", HttpStatusCode.OK);
        }

        return (string?)root["data"]?["createCommitOnBranch"]?["commit"]?["oid"] is { } oid && LivemapCommitSha.IsMatch(oid)
            ? oid
            : throw new GitHubApiException("GitHub commit returned no commit oid.", HttpStatusCode.OK);
    }

    public async Task<int> OpenAsync(string branch, string title, string body, CancellationToken cancellationToken)
    {
        RequireBranch(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        var payload = new JsonObject { ["title"] = title, ["head"] = branch, ["base"] = MainBranch, ["body"] = body };
        var opened = await GitHubHttp.SendAsync<NumberDto>(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/pulls", token, Json(payload)),
            "pull request open",
            cancellationToken).ConfigureAwait(false);
        return opened.Number > 0 ? opened.Number : throw new GitHubApiException("GitHub pull request open returned no number.", HttpStatusCode.OK);
    }

    public async Task<LivemapPullRequest?> GetAsync(int number, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _pullRequestsRead, cancellationToken).ConfigureAwait(false);
        var pull = await GitHubHttp.SendOrNullAsync<PullDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/pulls/{number}", token),
            "pull request read",
            cancellationToken).ConfigureAwait(false);
        return pull is null ? null : ToPullRequest(pull);
    }

    // Only the fork's own branch counts: a pull request from another repository under the same owner is not ours to touch.
    public async Task<IReadOnlyList<LivemapPullRequest>> FindOpenByBranchAsync(string branch, CancellationToken cancellationToken)
    {
        RequireBranch(branch);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _pullRequestsRead, cancellationToken).ConfigureAwait(false);
        var head = Uri.EscapeDataString($"{owner}:{branch}");
        var pulls = await GitHubHttp.SendAsync<List<PullDto>>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/pulls?state=open&head={head}&per_page={PageSize}", token),
            "pull request list",
            cancellationToken).ConfigureAwait(false);
        var fork = $"{owner}/{name}";
        return
        [
            .. pulls.Select(ToPullRequest).Where(pull =>
                string.Equals(pull.HeadRepo, fork, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pull.BaseRepo, fork, StringComparison.OrdinalIgnoreCase)
                && pull.HeadRef == branch),
        ];
    }

    // One page: the merge gate wants exactly one file, so a longer list fails it the same way.
    public async Task<IReadOnlyList<LivemapPullRequestFile>> ListFilesAsync(int number, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _pullRequestsRead, cancellationToken).ConfigureAwait(false);
        var files = await GitHubHttp.SendAsync<List<FileDto>>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/pulls/{number}/files?per_page={PageSize}", token),
            "pull request files read",
            cancellationToken).ConfigureAwait(false);
        return [.. files.Select(file => new LivemapPullRequestFile(file.Filename ?? string.Empty, file.Status ?? string.Empty, file.PreviousFilename))];
    }

    public async Task<IReadOnlyList<string>> GetParentsAsync(string commitSha, CancellationToken cancellationToken)
    {
        RequireSha(commitSha, nameof(commitSha));

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var commit = await GitHubHttp.SendAsync<CommitDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/git/commits/{commitSha}", token),
            "commit read",
            cancellationToken).ConfigureAwait(false);
        return [.. (commit.Parents ?? []).Select(parent => parent.Sha ?? string.Empty)];
    }

    public async Task<string?> GetFileModeAsync(string commitSha, string path, CancellationToken cancellationToken)
    {
        RequireSha(commitSha, nameof(commitSha));
        RequirePath(path);

        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..slash];
        var file = path[(slash + 1)..];
        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsRead, cancellationToken).ConfigureAwait(false);
        var tree = await GitHubHttp.SendOrNullAsync<TreeDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/git/trees/{commitSha}{(directory.Length == 0 ? string.Empty : ":" + directory)}", token),
            "tree read",
            cancellationToken).ConfigureAwait(false);
        return tree?.Tree?.FirstOrDefault(entry => entry.Path == file && entry.Type == "blob")?.Mode;
    }

    public async Task<IReadOnlyList<LivemapCheckRun>> ListCheckRunsAsync(string sha, CancellationToken cancellationToken)
    {
        RequireSha(sha, nameof(sha));

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _checksRead, cancellationToken).ConfigureAwait(false);
        var runs = new List<LivemapCheckRun>();
        for (var page = 1; page <= MaxCheckRunPages; page++)
        {
            var list = await GitHubHttp.SendAsync<CheckRunListDto>(
                http,
                GitHubHttp.Request(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"repos/{owner}/{name}/commits/{sha}/check-runs?filter=latest&per_page={PageSize}&page={page}"), token),
                "check runs read",
                cancellationToken).ConfigureAwait(false);
            if (list.CheckRuns is not { } batch)
            {
                throw new GitHubApiException("GitHub check runs read returned no check runs array.", HttpStatusCode.OK);
            }

            runs.AddRange(batch.Select(run => new LivemapCheckRun(run.Name ?? string.Empty, run.Status ?? string.Empty, run.Conclusion, run.App?.Slug)));
            if (batch.Count == 0 || runs.Count >= list.TotalCount)
            {
                return runs;
            }
        }

        throw new GitHubApiException("GitHub check runs read returned more pages than the cap.", HttpStatusCode.OK);
    }

    public async Task<LivemapMergeResult> MergeAsync(int number, string sha, string title, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        RequireSha(sha, nameof(sha));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsAndPullRequestsWrite, cancellationToken).ConfigureAwait(false);
        var payload = new JsonObject { ["merge_method"] = "squash", ["sha"] = sha, ["commit_title"] = title };
        using var request = GitHubHttp.Request(HttpMethod.Put, $"repos/{owner}/{name}/pulls/{number}/merge", token, Json(payload));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            return LivemapMergeResult.Refused;
        }

        GitHubHttp.EnsureSuccess(response, "pull request merge");
        return LivemapMergeResult.Merged;
    }

    public async Task ClosePullRequestAsync(int number, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendIfPresentAsync(
            http,
            GitHubHttp.Request(HttpMethod.Patch, $"repos/{owner}/{name}/pulls/{number}", token, Json(new JsonObject { ["state"] = "closed" })),
            "pull request close",
            cancellationToken).ConfigureAwait(false);
    }

    // A branch that is already gone answers 404 or 422, and either way it is deleted.
    public async Task DeleteBranchAsync(string branch, CancellationToken cancellationToken)
    {
        RequireBranch(branch);

        var (owner, name) = Fork();
        var token = await tokens.GetTokenAsync($"{owner}/{name}", _contentsWrite, cancellationToken).ConfigureAwait(false);
        using var request = GitHubHttp.Request(HttpMethod.Delete, $"repos/{owner}/{name}/git/refs/heads/{branch}", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity))
        {
            GitHubHttp.EnsureSuccess(response, "branch delete");
        }
    }

    public async Task<string> GetAppLoginAsync(CancellationToken cancellationToken)
    {
        var app = await GitHubHttp.SendAsync<AppDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, "app", jwt.CreateJwt()),
            "app read",
            cancellationToken).ConfigureAwait(false);
        return app.Slug is { Length: > 0 } slug && AppSlug().IsMatch(slug)
            ? $"{slug}[bot]"
            : throw new GitHubApiException("GitHub app read returned no usable slug.", HttpStatusCode.OK);
    }

    private static LivemapPullRequest ToPullRequest(PullDto pull) => new(
        pull.Number,
        pull.State ?? string.Empty,
        pull.Merged,
        pull.Draft,
        pull.Base?.Ref ?? string.Empty,
        pull.Base?.Repo?.FullName ?? string.Empty,
        pull.Head?.Ref ?? string.Empty,
        pull.Head?.Repo?.FullName ?? string.Empty,
        pull.Head?.Sha ?? string.Empty,
        pull.User?.Login ?? string.Empty,
        pull.User?.Type ?? string.Empty,
        pull.Commits);

    private static void RequireBranch(string branch)
    {
        if (!LivemapBuildBranch.IsMatch(branch))
        {
            throw new ArgumentException("The branch must be a livemap build branch.", nameof(branch));
        }
    }

    private static void RequireSha(string sha, string parameter)
    {
        if (!LivemapCommitSha.IsMatch(sha))
        {
            throw new ArgumentException("The commit must be given as a lowercase hex sha.", parameter);
        }
    }

    private static void RequirePath(string path)
    {
        if (path is null || !RepoPath().IsMatch(path) || path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The path must be a relative path of plain segments.", nameof(path));
        }
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static string Truncate(string text) => text.Length > MaxErrorChars ? text[..MaxErrorChars] : text;

    private (string Owner, string Name) Fork() => GitHubRepoName.Parse(options.Value.Repo, nameof(LivemapOptions.Repo));

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*\z")]
    private static partial Regex RepoPath();

    [GeneratedRegex(@"^[A-Za-z0-9-]+\z")]
    private static partial Regex AppSlug();

    private sealed record RefDto(RefObjectDto? Object);

    private sealed record RefObjectDto(string? Sha);

    private sealed record NumberDto(int Number);

    private sealed record PullDto(int Number, string? State, bool Merged, bool Draft, int Commits, UserDto? User, PullSideDto? Base, PullSideDto? Head);

    private sealed record UserDto(string? Login, string? Type);

    private sealed record PullSideDto(string? Ref, string? Sha, PullRepoDto? Repo);

    private sealed record PullRepoDto(string? FullName);

    private sealed record FileDto(string? Filename, string? Status, string? PreviousFilename);

    private sealed record CommitDto(List<ParentDto>? Parents);

    private sealed record ParentDto(string? Sha);

    private sealed record TreeDto(List<TreeEntryDto>? Tree);

    private sealed record TreeEntryDto(string? Path, string? Mode, string? Type);

    private sealed record CheckRunListDto(int TotalCount, List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(string? Name, string? Status, string? Conclusion, CheckRunAppDto? App);

    private sealed record CheckRunAppDto(string? Slug);

    private sealed record AppDto(string? Slug);
}
