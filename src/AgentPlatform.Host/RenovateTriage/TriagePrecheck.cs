using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Host.RenovateTriage;

internal static class TriagePrecheck
{
    // The caller has already checked that `pr` is the head it means to judge.
    public static async Task<PrecheckResult> RunAsync(
        IGitHubApiClient github,
        IUntrustedTextScanner scanner,
        string repo,
        int prNumber,
        GitHubPullRequest pr,
        CancellationToken cancellationToken)
    {
        var files = await github.ListPullRequestFilesAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        var commits = await github.ListCommitsAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        var reread = await github.GetPullRequestAsync(repo, prNumber, cancellationToken).ConfigureAwait(false);
        var shape = DiffShape.Classify(
            [.. files.Select(f => new DiffFile(f.Filename, f.Status, f.Patch))],
            pr.ChangedFiles,
            pr.HeadSha,
            [.. commits.Select(c => new DiffCommit(c.Sha, c.Message))],
            reread.HeadSha,
            TriageRules.StatedCommit(commits)?.Message ?? string.Empty);
        if (shape.Failure == DiffShapeFailure.HeadMoved)
        {
            return new PrecheckResult(true, PrecheckVerdict.Passed);
        }

        if (!shape.Passed)
        {
            return new PrecheckResult(false, PrecheckVerdict.DiffNotVersionOnly, Detail: shape.Failure.ToString());
        }

        var scan = await scanner.ScanAsync(pr.Body ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return scan.Outcome switch
        {
            ScanOutcome.Clean => new PrecheckResult(false, PrecheckVerdict.Passed, scan.Text),
            ScanOutcome.Flagged => new PrecheckResult(false, PrecheckVerdict.BodyFlagged),
            _ => new PrecheckResult(false, PrecheckVerdict.BodyUnchecked),
        };
    }
}
