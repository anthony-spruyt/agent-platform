using System.Net.Http.Json;

namespace AgentPlatform.Host.GitHub;

public sealed record GitHubPullRequest(
    int Number,
    string Title,
    string? Body,
    string State,
    bool Draft,
    bool Merged,
    string? MergeableState,
    string? AuthorLogin,
    long? AuthorId,
    string HeadRef,
    string HeadSha,
    string? HeadRepo,
    string? BaseRepo,
    string BaseRef,
    IReadOnlyList<string> Labels,
    int Commits);

public sealed record GitHubCheckRun(long Id, string Name, string Status, string? Conclusion, string? AppSlug);

public sealed record GitHubCheckSuite(long Id, string Status, string? Conclusion, string? AppSlug, int CheckRunCount);

public sealed record GitHubCommit(string Sha, string? AuthorLogin, long? AuthorId, string? AuthorEmail, string? CommitterEmail, bool Verified);

public sealed record GitHubPullRequestFile(string Filename, string? Patch);

public sealed record GitHubReview(long Id, string? AuthorLogin, string? AuthorAssociation, string State, string? CommitId, DateTimeOffset? SubmittedAt);

public sealed record GitHubIssueComment(long Id, string? AuthorLogin, string Body);

public interface IGitHubApiClient
{
    Task<GitHubPullRequest> GetPullRequestAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubCheckSuite>> ListCheckSuitesAsync(string repo, string sha, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubPullRequestFile>> ListPullRequestFilesAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubReview>> ListReviewsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListLabelsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubIssueComment>> ListIssueCommentsAsync(string repo, int number, CancellationToken cancellationToken);

    Task ApproveAsync(string repo, int number, string commitId, string body, CancellationToken cancellationToken);

    Task DismissReviewAsync(string repo, int number, long reviewId, string message, CancellationToken cancellationToken);

    Task AddLabelsAsync(string repo, int number, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task<long> CreateIssueCommentAsync(string repo, int number, string body, CancellationToken cancellationToken);

    Task UpdateIssueCommentAsync(string repo, long commentId, string body, CancellationToken cancellationToken);
}

public sealed class GitHubApiClient(HttpClient http, IGitHubInstallationTokenProvider tokens) : IGitHubApiClient
{
    public const int MaxPages = 50;

    private const int PageSize = 100;

    private static readonly Dictionary<string, string> _pullRequestsRead = new() { ["pull_requests"] = "read" };
    private static readonly Dictionary<string, string> _pullRequestsWrite = new() { ["pull_requests"] = "write" };
    private static readonly Dictionary<string, string> _checksRead = new() { ["checks"] = "read" };

    private sealed record PullRequestDto(
        int Number,
        string? Title,
        string? Body,
        string? State,
        bool Draft,
        bool Merged,
        string? MergeableState,
        UserDto? User,
        RefDto? Head,
        RefDto? Base,
        List<LabelDto>? Labels,
        int Commits);

    private sealed record UserDto(string? Login, long? Id);

    private sealed record RefDto(string? Ref, string? Sha, RepoDto? Repo);

    private sealed record RepoDto(string? FullName);

    private sealed record LabelDto(string? Name);

    private sealed record CheckRunsPage(List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(long Id, string? Name, string? Status, string? Conclusion, AppDto? App);

    private sealed record AppDto(string? Slug);

    private sealed record CheckSuitesPage(List<CheckSuiteDto>? CheckSuites);

    private sealed record CheckSuiteDto(long Id, string? Status, string? Conclusion, AppDto? App, int LatestCheckRunsCount);

    private sealed record CommitDto(string? Sha, UserDto? Author, CommitDetailDto? Commit);

    private sealed record CommitDetailDto(PersonDto? Author, PersonDto? Committer, VerificationDto? Verification);

    private sealed record PersonDto(string? Email);

    private sealed record VerificationDto(bool Verified);

    private sealed record FileDto(string? Filename, string? Patch);

    private sealed record IssueCommentDto(long Id, UserDto? User, string? Body);

    private sealed record ReviewDto(long Id, UserDto? User, string? AuthorAssociation, string? State, string? CommitId, DateTimeOffset? SubmittedAt);

    public async Task<GitHubPullRequest> GetPullRequestAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var token = await tokens.GetTokenAsync(repo, _pullRequestsRead, cancellationToken).ConfigureAwait(false);
        var pr = await GitHubHttp.SendAsync<PullRequestDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/pulls/{number}", token),
            "pull request read",
            cancellationToken).ConfigureAwait(false);

        return new GitHubPullRequest(
            pr.Number,
            pr.Title ?? string.Empty,
            pr.Body,
            pr.State ?? string.Empty,
            pr.Draft,
            pr.Merged,
            pr.MergeableState,
            pr.User?.Login,
            pr.User?.Id,
            pr.Head?.Ref ?? string.Empty,
            pr.Head?.Sha ?? string.Empty,
            pr.Head?.Repo?.FullName,
            pr.Base?.Repo?.FullName,
            pr.Base?.Ref ?? string.Empty,
            (pr.Labels ?? []).Select(l => l.Name).OfType<string>().ToList(),
            pr.Commits);
    }

    public async Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        if (!IsSha(sha))
        {
            throw new ArgumentException("The commit must be given as a hexadecimal sha.", nameof(sha));
        }

        var runs = await GetPagesAsync<CheckRunsPage, CheckRunDto>(
            repo,
            $"repos/{owner}/{name}/commits/{sha}/check-runs",
            _checksRead,
            "check runs list",
            page => page.CheckRuns,
            cancellationToken).ConfigureAwait(false);

        return [.. runs.Select(r => new GitHubCheckRun(r.Id, r.Name ?? string.Empty, r.Status ?? string.Empty, r.Conclusion, r.App?.Slug))];
    }

    public async Task<IReadOnlyList<GitHubCheckSuite>> ListCheckSuitesAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        if (!IsSha(sha))
        {
            throw new ArgumentException("The commit must be given as a hexadecimal sha.", nameof(sha));
        }

        var suites = await GetPagesAsync<CheckSuitesPage, CheckSuiteDto>(
            repo,
            $"repos/{owner}/{name}/commits/{sha}/check-suites",
            _checksRead,
            "check suites list",
            page => page.CheckSuites,
            cancellationToken).ConfigureAwait(false);

        return [.. suites.Select(s => new GitHubCheckSuite(s.Id, s.Status ?? string.Empty, s.Conclusion, s.App?.Slug, s.LatestCheckRunsCount))];
    }

    public async Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var commits = await GetPagesAsync<List<CommitDto>, CommitDto>(
            repo,
            $"repos/{owner}/{name}/pulls/{number}/commits",
            _pullRequestsRead,
            "commits list",
            page => page,
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. commits.Select(c => new GitHubCommit(
                c.Sha ?? string.Empty,
                c.Author?.Login,
                c.Author?.Id,
                c.Commit?.Author?.Email,
                c.Commit?.Committer?.Email,
                c.Commit?.Verification?.Verified ?? false)),
        ];
    }

    public async Task<IReadOnlyList<GitHubPullRequestFile>> ListPullRequestFilesAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var files = await GetPagesAsync<List<FileDto>, FileDto>(
            repo,
            $"repos/{owner}/{name}/pulls/{number}/files",
            _pullRequestsRead,
            "files list",
            page => page,
            cancellationToken).ConfigureAwait(false);

        return [.. files.Select(f => new GitHubPullRequestFile(f.Filename ?? string.Empty, f.Patch))];
    }

    public async Task<IReadOnlyList<GitHubReview>> ListReviewsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var reviews = await GetPagesAsync<List<ReviewDto>, ReviewDto>(
            repo,
            $"repos/{owner}/{name}/pulls/{number}/reviews",
            _pullRequestsRead,
            "reviews list",
            page => page,
            cancellationToken).ConfigureAwait(false);

        return [.. reviews.Select(r => new GitHubReview(r.Id, r.User?.Login, r.AuthorAssociation, r.State ?? string.Empty, r.CommitId, r.SubmittedAt))];
    }

    public async Task<IReadOnlyList<string>> ListLabelsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var labels = await GetPagesAsync<List<LabelDto>, LabelDto>(
            repo,
            $"repos/{owner}/{name}/issues/{number}/labels",
            _pullRequestsRead,
            "labels list",
            page => page,
            cancellationToken).ConfigureAwait(false);

        return [.. labels.Select(l => l.Name).OfType<string>()];
    }

    public async Task<IReadOnlyList<GitHubIssueComment>> ListIssueCommentsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var comments = await GetPagesAsync<List<IssueCommentDto>, IssueCommentDto>(
            repo,
            $"repos/{owner}/{name}/issues/{number}/comments",
            _pullRequestsRead,
            "comments list",
            page => page,
            cancellationToken).ConfigureAwait(false);

        return [.. comments.Select(c => new GitHubIssueComment(c.Id, c.User?.Login, c.Body ?? string.Empty))];
    }

    public async Task ApproveAsync(string repo, int number, string commitId, string body, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        if (!IsSha(commitId))
        {
            throw new ArgumentException("The commit must be given as a hexadecimal sha.", nameof(commitId));
        }

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/pulls/{number}/reviews", token, JsonContent.Create(new { @event = "APPROVE", commit_id = commitId, body }, options: GitHubJson.Options)),
            "review create",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DismissReviewAsync(string repo, int number, long reviewId, string message, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reviewId);

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Put, $"repos/{owner}/{name}/pulls/{number}/reviews/{reviewId}/dismissals", token, JsonContent.Create(new { message }, options: GitHubJson.Options)),
            "review dismissal",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AddLabelsAsync(string repo, int number, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0)
        {
            throw new ArgumentException("At least one label is required.", nameof(labels));
        }

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/issues/{number}/labels", token, JsonContent.Create(new { labels }, options: GitHubJson.Options)),
            "labels add",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CreateIssueCommentAsync(string repo, int number, string body, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        var created = await GitHubHttp.SendAsync<IssueCommentDto>(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/issues/{number}/comments", token, JsonContent.Create(new { body }, options: GitHubJson.Options)),
            "comment create",
            cancellationToken).ConfigureAwait(false);
        return created.Id;
    }

    public async Task UpdateIssueCommentAsync(string repo, long commentId, string body, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commentId);

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Patch, $"repos/{owner}/{name}/issues/comments/{commentId}", token, JsonContent.Create(new { body }, options: GitHubJson.Options)),
            "comment update",
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSha(string? value) =>
        value is { Length: >= 7 and <= 64 } && value.All(char.IsAsciiHexDigit);

    private async Task<List<TItem>> GetPagesAsync<TPage, TItem>(
        string repo,
        string path,
        IReadOnlyDictionary<string, string> permissions,
        string operation,
        Func<TPage, List<TItem>?> items,
        CancellationToken cancellationToken)
        where TPage : class
    {
        var token = await tokens.GetTokenAsync(repo, permissions, cancellationToken).ConfigureAwait(false);
        var all = new List<TItem>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = await GitHubHttp.SendAsync<TPage>(
                http,
                GitHubHttp.Request(HttpMethod.Get, $"{path}?per_page={PageSize}&page={page}", token),
                operation,
                cancellationToken).ConfigureAwait(false);
            var batch = items(response) ?? [];
            all.AddRange(batch);
            if (batch.Count < PageSize)
            {
                return all;
            }
        }

        throw new GitHubApiException($"GitHub {operation} exceeded {MaxPages} pages.");
    }
}
