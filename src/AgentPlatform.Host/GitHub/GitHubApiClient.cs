using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

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
    int Commits,
    int ChangedFiles = 0);

public sealed record GitHubCheckRun(long Id, string Name, string Status, string? Conclusion, string? AppSlug, long? AppId = null, string? ExternalId = null);

public sealed record GitHubCheckSuite(long Id, string Status, string? Conclusion, string? AppSlug, int CheckRunCount, long? AppId = null);

public sealed record GitHubCommit(string Sha, string? AuthorLogin, long? AuthorId, string? AuthorEmail, string? CommitterEmail, bool Verified, string Message = "");

public sealed record GitHubPullRequestFile(string Filename, string? Patch, string Status = "modified");

public sealed record GitHubReview(long Id, string? AuthorLogin, string? AuthorAssociation, string State, string? CommitId, DateTimeOffset? SubmittedAt);

public sealed record GitHubIssueComment(long Id, string? AuthorLogin, string Body);

public sealed record GitHubCheckRunWrite(string Name, string HeadSha, string ExternalId, string Status, string? Conclusion, string Title, string Summary);

public sealed record GitHubRelease(string TagName, string? Name, string? Body);

public sealed record GitHubJob(long Id, long RunId, int RunAttempt, string Name, string? Conclusion);

public sealed record GitHubCommitStatus(string Context, string State);

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

    Task<long> CreateCheckRunAsync(string repo, GitHubCheckRunWrite run, CancellationToken cancellationToken);

    Task UpdateCheckRunAsync(string repo, long checkRunId, GitHubCheckRunWrite run, CancellationToken cancellationToken);

    Task<GitHubCheckRun?> FindCheckRunAsync(string repo, string sha, string name, string externalId, CancellationToken cancellationToken);

    Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string sourceRepo, string tag, CancellationToken cancellationToken);

    Task<string?> GetFileContentAsync(string repo, string sourceRepo, string path, string gitRef, int maxChars, CancellationToken cancellationToken);

    Task<GitHubJob?> GetJobAsync(string repo, long jobId, CancellationToken cancellationToken);

    Task<string?> GetJobLogTailAsync(string repo, long jobId, int maxChars, CancellationToken cancellationToken);

    Task RerunFailedJobsAsync(string repo, long runId, CancellationToken cancellationToken);

    Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubCommitStatus>> ListCommitStatusesAsync(string repo, string sha, CancellationToken cancellationToken);
}

public sealed class GitHubApiClient(HttpClient http, IGitHubInstallationTokenProvider tokens, IOptions<GitHubAppOptions> appOptions) : IGitHubApiClient
{
    public const int MaxPages = 50;

    public const string TriageCheckExternalIdPrefix = "renovate-triage/";

    private const int PageSize = 100;

    private readonly long _ownAppId = GitHubAppOptions.RequireAppId(appOptions.Value.AppId);

    private static readonly Dictionary<string, string> _pullRequestsRead = new() { ["pull_requests"] = "read" };
    private static readonly Dictionary<string, string> _pullRequestsWrite = new() { ["pull_requests"] = "write" };
    private static readonly Dictionary<string, string> _checksRead = new() { ["checks"] = "read" };
    private static readonly Dictionary<string, string> _checksWrite = new() { ["checks"] = "write" };
    private static readonly Dictionary<string, string> _contentsRead = new() { ["contents"] = "read" };
    private static readonly Dictionary<string, string> _actionsRead = new() { ["actions"] = "read" };
    private static readonly Dictionary<string, string> _actionsWrite = new() { ["actions"] = "write" };
    private static readonly Dictionary<string, string> _statusesRead = new() { ["statuses"] = "read" };

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
        int Commits,
        int ChangedFiles);

    private sealed record UserDto(string? Login, long? Id);

    private sealed record RefDto(string? Ref, string? Sha, RepoDto? Repo);

    private sealed record RepoDto(string? FullName);

    private sealed record LabelDto(string? Name);

    private sealed record CheckRunsPage(List<CheckRunDto>? CheckRuns);

    private sealed record CheckRunDto(long Id, string? Name, string? Status, string? Conclusion, AppDto? App, string? ExternalId);

    private sealed record AppDto(long? Id, string? Slug);

    private sealed record CheckSuitesPage(List<CheckSuiteDto>? CheckSuites);

    private sealed record CheckSuiteDto(long Id, string? Status, string? Conclusion, AppDto? App, int LatestCheckRunsCount);

    private sealed record CommitDto(string? Sha, UserDto? Author, CommitDetailDto? Commit);

    private sealed record CommitDetailDto(string? Message, PersonDto? Author, PersonDto? Committer, VerificationDto? Verification);

    private sealed record PersonDto(string? Email);

    private sealed record VerificationDto(bool Verified);

    private sealed record FileDto(string? Filename, string? Patch, string? Status);

    private sealed record IssueCommentDto(long Id, UserDto? User, string? Body);

    private sealed record ReviewDto(long Id, UserDto? User, string? AuthorAssociation, string? State, string? CommitId, DateTimeOffset? SubmittedAt);

    private sealed record ReleaseDto(string? TagName, string? Name, string? Body);

    private sealed record JobDto(long Id, long RunId, int RunAttempt, string? Name, string? Conclusion);

    private sealed record CombinedStatusPage(List<StatusDto>? Statuses);

    private sealed record StatusDto(string? Context, string? State);

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
            pr.Commits,
            pr.ChangedFiles);
    }

    public async Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var runs = await ReadCheckRunsAsync(repo, sha, cancellationToken).ConfigureAwait(false);
        return [.. runs.Where(r => !IsOwnTriageRun(r))];
    }

    private async Task<List<GitHubCheckRun>> ReadCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken)
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

        return [.. runs.Select(r => new GitHubCheckRun(r.Id, r.Name ?? string.Empty, r.Status ?? string.Empty, r.Conclusion, r.App?.Slug, r.App?.Id, r.ExternalId))];
    }

    private bool IsOwnTriageRun(GitHubCheckRun run) =>
        run.AppId == _ownAppId
        && run.ExternalId?.StartsWith(TriageCheckExternalIdPrefix, StringComparison.Ordinal) == true;

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

        return
        [
            .. suites
                .Where(s => s.App?.Id != _ownAppId)
                .Select(s => new GitHubCheckSuite(s.Id, s.Status ?? string.Empty, s.Conclusion, s.App?.Slug, s.LatestCheckRunsCount, s.App?.Id)),
        ];
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
                c.Commit?.Verification?.Verified ?? false,
                c.Commit?.Message ?? string.Empty)),
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

        return [.. files.Select(f => new GitHubPullRequestFile(f.Filename ?? string.Empty, f.Patch, f.Status ?? string.Empty))];
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

    public async Task<long> CreateCheckRunAsync(string repo, GitHubCheckRunWrite run, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentNullException.ThrowIfNull(run);
        if (!IsSha(run.HeadSha))
        {
            throw new ArgumentException("The commit must be given as a hexadecimal sha.", nameof(run));
        }

        var payload = CheckRunPayload(run);
        payload["name"] = run.Name;
        payload["head_sha"] = run.HeadSha;
        payload["external_id"] = run.ExternalId;

        var token = await tokens.GetTokenAsync(repo, _checksWrite, cancellationToken).ConfigureAwait(false);
        var created = await GitHubHttp.SendAsync<CheckRunDto>(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/check-runs", token, JsonContent.Create(payload)),
            "check run create",
            cancellationToken).ConfigureAwait(false);
        return created.Id;
    }

    public async Task UpdateCheckRunAsync(string repo, long checkRunId, GitHubCheckRunWrite run, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkRunId);
        ArgumentNullException.ThrowIfNull(run);

        var token = await tokens.GetTokenAsync(repo, _checksWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Patch, $"repos/{owner}/{name}/check-runs/{checkRunId}", token, JsonContent.Create(CheckRunPayload(run))),
            "check run update",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubCheckRun?> FindCheckRunAsync(string repo, string sha, string name, string externalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(externalId);

        var runs = await ReadCheckRunsAsync(repo, sha, cancellationToken).ConfigureAwait(false);
        return runs.FirstOrDefault(r =>
            r.Name == name
            && r.ExternalId == externalId
            && r.AppId == _ownAppId);
    }

    private static JsonObject CheckRunPayload(GitHubCheckRunWrite run)
    {
        var payload = new JsonObject
        {
            ["status"] = run.Status,
            ["output"] = new JsonObject { ["title"] = run.Title, ["summary"] = run.Summary },
        };
        if (run.Conclusion is not null)
        {
            payload["conclusion"] = run.Conclusion;
        }

        return payload;
    }

    public async Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string sourceRepo, string tag, CancellationToken cancellationToken)
    {
        GitHubRepoName.Parse(repo, nameof(repo));
        var (owner, name) = GitHubRepoName.Parse(sourceRepo, nameof(sourceRepo));
        var path = PlainPath(tag, nameof(tag));

        var token = await tokens.GetTokenAsync(repo, _contentsRead, cancellationToken).ConfigureAwait(false);
        var release = await GitHubHttp.SendOrNullAsync<ReleaseDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/releases/tags/{path}", token),
            "release read",
            cancellationToken).ConfigureAwait(false);

        return release is null ? null : new GitHubRelease(release.TagName ?? tag, release.Name, release.Body);
    }

    public async Task<string?> GetFileContentAsync(string repo, string sourceRepo, string path, string gitRef, int maxChars, CancellationToken cancellationToken)
    {
        GitHubRepoName.Parse(repo, nameof(repo));
        var (owner, name) = GitHubRepoName.Parse(sourceRepo, nameof(sourceRepo));
        var filePath = PlainPath(path, nameof(path));
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var token = await tokens.GetTokenAsync(repo, _contentsRead, cancellationToken).ConfigureAwait(false);
        var request = GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/contents/{filePath}?ref={Uri.EscapeDataString(gitRef)}", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        return await GitHubHttp.ReadTextOrNullAsync(http, request, "file read", maxChars, keepTail: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubJob?> GetJobAsync(string repo, long jobId, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jobId);

        var token = await tokens.GetTokenAsync(repo, _actionsRead, cancellationToken).ConfigureAwait(false);
        var job = await GitHubHttp.SendOrNullAsync<JobDto>(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/actions/jobs/{jobId}", token),
            "job read",
            cancellationToken).ConfigureAwait(false);

        return job is null ? null : new GitHubJob(job.Id, job.RunId, job.RunAttempt, job.Name ?? string.Empty, job.Conclusion);
    }

    public async Task<string?> GetJobLogTailAsync(string repo, long jobId, int maxChars, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jobId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var token = await tokens.GetTokenAsync(repo, _actionsRead, cancellationToken).ConfigureAwait(false);
        return await GitHubHttp.ReadTextOrNullAsync(
            http,
            GitHubHttp.Request(HttpMethod.Get, $"repos/{owner}/{name}/actions/jobs/{jobId}/logs", token),
            "job log read",
            maxChars,
            keepTail: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RerunFailedJobsAsync(string repo, long runId, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);

        var token = await tokens.GetTokenAsync(repo, _actionsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendAsync(
            http,
            GitHubHttp.Request(HttpMethod.Post, $"repos/{owner}/{name}/actions/runs/{runId}/rerun-failed-jobs", token),
            "failed jobs rerun",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var token = await tokens.GetTokenAsync(repo, _pullRequestsWrite, cancellationToken).ConfigureAwait(false);
        await GitHubHttp.SendIfPresentAsync(
            http,
            GitHubHttp.Request(HttpMethod.Delete, $"repos/{owner}/{name}/issues/{number}/labels/{Uri.EscapeDataString(label)}", token),
            "label remove",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubCommitStatus>> ListCommitStatusesAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var (owner, name) = GitHubRepoName.Parse(repo, nameof(repo));
        if (!IsSha(sha))
        {
            throw new ArgumentException("The commit must be given as a hexadecimal sha.", nameof(sha));
        }

        var statuses = await GetPagesAsync<CombinedStatusPage, StatusDto>(
            repo,
            $"repos/{owner}/{name}/commits/{sha}/status",
            _statusesRead,
            "commit statuses list",
            page => page.Statuses,
            cancellationToken).ConfigureAwait(false);

        return [.. statuses.Select(s => new GitHubCommitStatus(s.Context ?? string.Empty, s.State ?? string.Empty))];
    }

    private static string PlainPath(string value, string paramName)
    {
        var segments = value?.Split('/');
        if (segments is null || segments.Any(s => s.Length == 0 || s is "." or ".." || !s.All(IsPathChar)))
        {
            throw new ArgumentException("The value must be a relative path of plain segments.", paramName);
        }

        return value!;
    }

    private static bool IsPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+' or '~';

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
