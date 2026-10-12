using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.Support;

internal sealed class FakeGitHubApi : IGitHubApiClient
{
    public const string Sha = "0123456789abcdef0123456789abcdef01234567";

    public GitHubPullRequest PullRequest { get; set; } = Renovate();

    public List<GitHubReview> Reviews { get; } = [];

    public List<GitHubCheckRun> Checks { get; } = [new(1, "build", "completed", "success", "github-actions")];

    public List<GitHubCheckSuite> Suites { get; } = [new(1, "completed", "success", "github-actions", 1)];

    public List<GitHubCommit> Commits { get; } = [RenovateCommit()];

    public List<GitHubPullRequestFile> Files { get; } = [new("package.json", "@@ -1 +1 @@\n-\"dep\": \"1.0.0\"\n+\"dep\": \"1.0.1\"")];

    public List<GitHubIssueComment> Comments { get; } = [];

    public Exception? Failure { get; set; }

    public Exception? WriteFailure { get; set; }

    public Action? OnDismiss { get; set; }

    public List<string> Writes { get; } = [];

    public List<(string CommitId, string Body)> Approvals { get; } = [];

    public List<string> AddedLabels { get; } = [];

    public List<long> Dismissed { get; } = [];

    public List<string> Calls { get; } = [];

    public Action? OnPullRequestRead { get; set; }

    public static GitHubCommit RenovateCommit(string sha = "c0ffee0") =>
        new(sha, "renovate[bot]", TriageRules.RenovateUserId, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true);

    public const string Body = "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://example.test/dep) | `1.0.0` -> `1.0.1` |\n\n---\n\n### Release Notes\n";

    public static GitHubPullRequest Renovate(string branch = "renovate/czkawka", string sha = Sha, string? body = null) =>
        new(7, "Update dep", body ?? Body, "open", false, false, "clean", "renovate[bot]", TriageRules.RenovateUserId, branch, sha, "anthony-spruyt/widgets", "anthony-spruyt/widgets", "main", ["dependencies"], 1);

    public Task<GitHubPullRequest> GetPullRequestAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("pr");
        OnPullRequestRead?.Invoke();
        return Result(PullRequest);
    }

    public Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        Calls.Add($"checks:{sha}");
        return Result<IReadOnlyList<GitHubCheckRun>>([.. Checks]);
    }

    public Task<IReadOnlyList<GitHubCheckSuite>> ListCheckSuitesAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        Calls.Add($"suites:{sha}");
        return Result<IReadOnlyList<GitHubCheckSuite>>([.. Suites]);
    }

    public Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("commits");
        return Result<IReadOnlyList<GitHubCommit>>([.. Commits]);
    }

    public Task<IReadOnlyList<GitHubPullRequestFile>> ListPullRequestFilesAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("files");
        return Result<IReadOnlyList<GitHubPullRequestFile>>([.. Files]);
    }

    public Task<IReadOnlyList<GitHubReview>> ListReviewsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("reviews");
        return Result<IReadOnlyList<GitHubReview>>([.. Reviews]);
    }

    public Task<IReadOnlyList<string>> ListLabelsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("labels");
        return Result(PullRequest.Labels);
    }

    public Task<IReadOnlyList<GitHubIssueComment>> ListIssueCommentsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("comments");
        return Result<IReadOnlyList<GitHubIssueComment>>([.. Comments]);
    }

    public Task ApproveAsync(string repo, int number, string commitId, string body, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add($"approve:{commitId}");
        Approvals.Add((commitId, body));
        Reviews.Add(new GitHubReview(1000 + Reviews.Count, TriageRules.TriageBotLogin, "CONTRIBUTOR", "APPROVED", commitId, DateTimeOffset.UnixEpoch));
        return Task.CompletedTask;
    }

    public Task DismissReviewAsync(string repo, int number, long reviewId, string message, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        try
        {
            OnDismiss?.Invoke();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }

        Writes.Add($"dismiss:{reviewId}");
        Dismissed.Add(reviewId);
        var index = Reviews.FindIndex(r => r.Id == reviewId);
        if (index >= 0)
        {
            Reviews[index] = Reviews[index] with { State = "DISMISSED" };
        }

        return Task.CompletedTask;
    }

    public Task AddLabelsAsync(string repo, int number, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add($"labels:{string.Join(',', labels)}");
        AddedLabels.AddRange(labels);
        PullRequest = PullRequest with { Labels = [.. PullRequest.Labels.Union(labels)] };
        return Task.CompletedTask;
    }

    public Task<long> CreateIssueCommentAsync(string repo, int number, string body, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException<long>(WriteFailure);
        }

        var id = 500 + Comments.Count;
        Writes.Add($"comment-create:{id}");
        Comments.Add(new GitHubIssueComment(id, TriageRules.TriageBotLogin, body));
        return Task.FromResult((long)id);
    }

    public Task UpdateIssueCommentAsync(string repo, long commentId, string body, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add($"comment-update:{commentId}");
        var index = Comments.FindIndex(c => c.Id == commentId);
        if (index >= 0)
        {
            Comments[index] = Comments[index] with { Body = body };
        }

        return Task.CompletedTask;
    }

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}

internal sealed class FakeJev : IJevClient
{
    public JevAnswers Answers { get; set; } = new(0.04, "patch", 0.78);

    public Exception? Failure { get; set; }

    public int? FailuresBeforeAnswer { get; set; }

    public List<string?> Bodies { get; } = [];

    public List<string?> Diffs { get; } = [];

    public Task<JevAnswers> AskAsync(string? prBody, string? prDiff, CancellationToken cancellationToken)
    {
        Bodies.Add(prBody);
        Diffs.Add(prDiff);
        if (Failure is not null && FailuresBeforeAnswer is not <= 0)
        {
            FailuresBeforeAnswer--;
            return Task.FromException<JevAnswers>(Failure);
        }

        return Task.FromResult(Answers);
    }
}
