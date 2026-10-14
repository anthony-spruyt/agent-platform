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

    public List<string> DismissalMessages { get; } = [];

    public List<string> Calls { get; } = [];

    public Action? OnPullRequestRead { get; set; }

    public Func<Task>? OnCheckRunsRead { get; set; }

    public const string CommitMessage = "chore(deps): update dep to v1.0.1\n\n| datasource | package | from | to |\n| --- | --- | --- | --- |\n| npm | dep | 1.0.0 | 1.0.1 |";

    public static GitHubCommit RenovateCommit(string sha = Sha, string message = CommitMessage) =>
        new(sha, "renovate[bot]", TriageRules.RenovateUserId, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true, message);

    public const string Body = "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://example.test/dep) | `1.0.0` -> `1.0.1` |\n\n---\n\n### Release Notes\n";

    public static GitHubPullRequest Renovate(string branch = "renovate/czkawka", string sha = Sha, string? body = null) =>
        new(7, "Update dep", body ?? Body, "open", false, false, "clean", "renovate[bot]", TriageRules.RenovateUserId, branch, sha, "anthony-spruyt/widgets", "anthony-spruyt/widgets", "main", ["dependencies"], 1, 1);

    public Task<GitHubPullRequest> GetPullRequestAsync(string repo, int number, CancellationToken cancellationToken)
    {
        Calls.Add("pr");
        OnPullRequestRead?.Invoke();
        return Result(PullRequest);
    }

    public async Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        Calls.Add($"checks:{sha}");
        if (OnCheckRunsRead is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        return await Result<IReadOnlyList<GitHubCheckRun>>([.. Checks]).ConfigureAwait(false);
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
        DismissalMessages.Add(message);
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

    public List<GitHubCommitStatus> Statuses { get; } = [new("renovate/stability-days", "success")];

    public List<string> RemovedLabels { get; } = [];

    public Task<IReadOnlyList<GitHubCommitStatus>> ListCommitStatusesAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        Calls.Add($"statuses:{sha}");
        return Result<IReadOnlyList<GitHubCommitStatus>>([.. Statuses]);
    }

    public Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add($"label-remove:{label}");
        RemovedLabels.Add(label);
        PullRequest = PullRequest with { Labels = [.. PullRequest.Labels.Where(l => l != label)] };
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

    public List<(long Id, GitHubCheckRunWrite Run)> StoredCheckRuns { get; } = [];

    public List<string> CheckRunWrites { get; } = [];

    public Task<long> CreateCheckRunAsync(string repo, GitHubCheckRunWrite run, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException<long>(WriteFailure);
        }

        var id = 700 + StoredCheckRuns.Count;
        CheckRunWrites.Add($"check-create:{id}:{run.Status}");
        StoredCheckRuns.Add((id, run));
        return Task.FromResult((long)id);
    }

    public Task UpdateCheckRunAsync(string repo, long checkRunId, GitHubCheckRunWrite run, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        CheckRunWrites.Add($"check-update:{checkRunId}:{run.Status}");
        var index = StoredCheckRuns.FindIndex(c => c.Id == checkRunId);
        StoredCheckRuns[index] = (checkRunId, StoredCheckRuns[index].Run with { Status = run.Status, Conclusion = run.Conclusion, Title = run.Title, Summary = run.Summary });
        return Task.CompletedTask;
    }

    public Task<GitHubCheckRun?> FindCheckRunAsync(string repo, string sha, string name, string externalId, CancellationToken cancellationToken)
    {
        Calls.Add($"check-find:{sha}");
        var found = StoredCheckRuns.Where(c => c.Run.HeadSha == sha && c.Run.Name == name && c.Run.ExternalId == externalId).Select(c => (GitHubCheckRun?)new GitHubCheckRun(c.Id, c.Run.Name, c.Run.Status, c.Run.Conclusion, null, null, c.Run.ExternalId)).FirstOrDefault();
        return Failure is null ? Task.FromResult(found) : Task.FromException<GitHubCheckRun?>(Failure);
    }

    public Dictionary<string, GitHubRelease> Releases { get; } = [];

    public Dictionary<string, string> FileContents { get; } = [];

    public Dictionary<long, GitHubJob> Jobs { get; } = [];

    public Dictionary<long, string> JobLogs { get; } = [];

    public List<long> Reruns { get; } = [];

    public Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string sourceRepo, string tag, CancellationToken cancellationToken)
    {
        Calls.Add($"release:{sourceRepo}:{tag}");
        return Result(Releases.GetValueOrDefault($"{sourceRepo}:{tag}"));
    }

    public Task<string?> GetFileContentAsync(string repo, string sourceRepo, string path, string gitRef, int maxChars, CancellationToken cancellationToken)
    {
        Calls.Add($"file:{sourceRepo}:{path}@{gitRef}");
        var text = FileContents.GetValueOrDefault($"{sourceRepo}:{path}@{gitRef}");
        return Result(text is null ? null : text[..Math.Min(text.Length, maxChars)]);
    }

    public Task<GitHubJob?> GetJobAsync(string repo, long jobId, CancellationToken cancellationToken)
    {
        Calls.Add($"job:{jobId}");
        return Result(Jobs.GetValueOrDefault(jobId));
    }

    public Task<string?> GetJobLogTailAsync(string repo, long jobId, int maxChars, CancellationToken cancellationToken)
    {
        Calls.Add($"log:{jobId}");
        var log = JobLogs.GetValueOrDefault(jobId);
        return Result(log is null ? null : log[Math.Max(0, log.Length - maxChars)..]);
    }

    public Task RerunFailedJobsAsync(string repo, long runId, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add($"rerun:{runId}");
        Reruns.Add(runId);
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

    public CiFailureKind CiKind { get; set; } = CiFailureKind.Unsure;

    public List<string> CiLogs { get; } = [];

    public Func<Task>? OnClassifyCi { get; set; }

    public async Task<CiFailureKind> ClassifyCiAsync(string ciLogs, CancellationToken cancellationToken)
    {
        CiLogs.Add(ciLogs);
        if (OnClassifyCi is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        return Failure is null ? CiKind : throw Failure;
    }

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
