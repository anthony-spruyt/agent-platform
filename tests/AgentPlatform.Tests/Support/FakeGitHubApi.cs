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

    public Exception? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public Action? OnPullRequestRead { get; set; }

    public static GitHubCommit RenovateCommit(string sha = "c0ffee0") =>
        new(sha, "renovate[bot]", TriageRules.RenovateUserId, "29139614+renovate[bot]@users.noreply.github.com", "noreply@github.com", true);

    public const string Body = "This PR contains the following updates:\n\n| Package | Change |\n|---|---|\n| [dep](https://example.test/dep) | `1.0.0` -> `1.0.1` |\n\n---\n\n### Release Notes\n";

    public static GitHubPullRequest Renovate(string branch = "renovate/czkawka", string sha = Sha, string? body = null) =>
        new(7, "Update dep", body ?? Body, "open", false, false, "clean", "renovate[bot]", TriageRules.RenovateUserId, branch, sha, "octo/widgets", "octo/widgets", "main", ["dependencies"], 1);

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

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}

internal sealed class FakeJev : IJevClient
{
    public JevAnswers Answers { get; set; } = new(0.04, "patch", 0.78);

    public Exception? Failure { get; set; }

    public List<string?> Bodies { get; } = [];

    public List<string?> Diffs { get; } = [];

    public Task<JevAnswers> AskAsync(string? prBody, string? prDiff, CancellationToken cancellationToken)
    {
        Bodies.Add(prBody);
        Diffs.Add(prDiff);
        return Failure is null ? Task.FromResult(Answers) : Task.FromException<JevAnswers>(Failure);
    }
}
