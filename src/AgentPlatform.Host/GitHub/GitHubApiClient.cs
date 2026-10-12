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
    string HeadRef,
    string HeadSha,
    string? HeadRepo,
    string BaseRef,
    IReadOnlyList<string> Labels);

public sealed record GitHubCheckRun(long Id, string Name, string Status, string? Conclusion, string? AppSlug);

public sealed record GitHubReview(long Id, string? AuthorLogin, string State, string? CommitId, DateTimeOffset? SubmittedAt);

public interface IGitHubApiClient
{
    Task<GitHubPullRequest> GetPullRequestAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubReview>> ListReviewsAsync(string repo, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListLabelsAsync(string repo, int number, CancellationToken cancellationToken);
}

public sealed class GitHubApiClient(HttpClient http, IGitHubInstallationTokenProvider tokens) : IGitHubApiClient
{
    public const int MaxPages = 50;

    private const int PageSize = 100;

    private static readonly Dictionary<string, string> _pullRequestsRead = new() { ["pull_requests"] = "read" };
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
        List<LabelDto>? Labels);

    private sealed record UserDto(string? Login);

    private sealed record RefDto(string? Ref, string? Sha, RepoDto? Repo);

    private sealed record RepoDto(string? FullName);

    private sealed record LabelDto(string? Name);

    private sealed record CheckRunsPage(List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(long Id, string? Name, string? Status, string? Conclusion, AppDto? App);

    private sealed record AppDto(string? Slug);

    private sealed record ReviewDto(long Id, UserDto? User, string? State, string? CommitId, DateTimeOffset? SubmittedAt);

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
            pr.Head?.Ref ?? string.Empty,
            pr.Head?.Sha ?? string.Empty,
            pr.Head?.Repo?.FullName,
            pr.Base?.Ref ?? string.Empty,
            (pr.Labels ?? []).Select(l => l.Name).OfType<string>().ToList());
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

        return [.. reviews.Select(r => new GitHubReview(r.Id, r.User?.Login, r.State ?? string.Empty, r.CommitId, r.SubmittedAt))];
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
