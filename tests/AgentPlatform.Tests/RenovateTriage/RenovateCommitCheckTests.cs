using System.Net;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.RenovateTriage;

public class RenovateCommitCheckTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string RenovateSha = FakeGitHubApi.Sha;
    private const string MergeSha = FakeGitHubApi.MergeSha;
    private const string BaseParent = FakeGitHubApi.BaseParentSha;
    private const string BranchPoint = FakeGitHubApi.BranchPointSha;
    private const string RenovateParent = "5555555555555555555555555555555555555555";
    private const string SecondMergeSha = "6666666666666666666666666666666666666666";
    private const string SecondBaseParent = "7777777777777777777777777777777777777777";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHubCommit Renovate(string sha = RenovateSha, string parent = RenovateParent) =>
        FakeGitHubApi.RenovateCommit(sha) with { Parents = [parent] };

    private static GitHubCommit Merge(string sha, string firstParent, string secondParent, string author = "alice") =>
        new(sha, author, 5, "alice@example.test", "noreply@github.com", true, "Merge branch 'main' into renovate/dep", [firstParent, secondParent]);

    private static GitHubCompareFile File(string name, string sha, string status = "modified", string? previous = null) => new(name, status, sha, previous);

    private static string RenovateChange => $"{BranchPoint}...{RenovateSha}";

    private static GitHubComparison Ahead(params GitHubCompareFile[] files) => new("ahead", null, files);

    private static GitHubComparison Behind() => new("behind", null, []);

    private static FakeGitHubApi HonestMerge() => new FakeGitHubApi().MergeBaseIn();

    private static Task<bool> Check(FakeGitHubApi github) =>
        RenovateCommitCheck.IsRenovateOnlyAsync(github, Repo, github.PullRequest, [.. github.Commits], Ct);

    [Fact]
    public async Task AnAllRenovatePr_IsAcceptedWithoutReadingAnyComparison()
    {
        var github = new FakeGitHubApi();

        Assert.True(await Check(github));
        Assert.DoesNotContain(github.Calls, c => c.StartsWith("compare:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHonestCleanMerge_IsAccepted()
    {
        var github = HonestMerge();

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task TheMergeAuthorDoesNotMatter()
    {
        var github = HonestMerge();
        github.Commits[1] = github.Commits[1] with { AuthorLogin = "someone-else", AuthorId = 99, Verified = false };

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task AMergeOfNothingNew_IsAccepted()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = new GitHubComparison("identical", null, []);
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = new GitHubComparison("identical", null, []);

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task SeveralMerges_AreJudgedAgainstTheNewestBaseParent()
    {
        var github = HonestMerge();
        github.Commits.Add(Merge(SecondMergeSha, MergeSha, SecondBaseParent));
        github.PullRequest = github.PullRequest with { HeadSha = SecondMergeSha, Commits = 3 };
        github.Comparisons[$"main...{SecondBaseParent}"] = Behind();
        github.Comparisons[$"{RenovateSha}...{SecondMergeSha}"] = Ahead(File("README.md", "ccc"));
        github.Comparisons[$"{BranchPoint}...{SecondBaseParent}"] = Ahead(File("README.md", "ccc"));

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task AMergeThatKeepsRenovatesVersionOfAFileTheBaseChanged_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("src/new.txt", "bbb", "added"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeThatDropsEverythingTheBaseChanged_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = new GitHubComparison("identical", null, []);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeWithAnExtraEdit_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("README.md", "aaa"), File("src/new.txt", "bbb", "added"), File("deploy.sh", "ccc"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeWhoseFileDiffersFromTheBaseVersion_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("README.md", "changed-sha"), File("src/new.txt", "bbb", "added"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeWhoseFileHasAnotherStatus_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("README.md", "aaa", "added"), File("src/new.txt", "bbb", "added"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeWhereTheBaseTouchedAFileRenovateAlsoChanged_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("package.json", "mixed-blob"));
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(File("package.json", "base-blob"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AMergeThatTakesTheBaseVersionOfAFileRenovateChanged_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("package.json", "base-blob"));
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(File("package.json", "base-blob"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ABaseChangeToThePreviousNameOfAFileRenovateRenamed_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[RenovateChange] = Ahead(File("new.json", "r1", "renamed", "old.json"));
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("old.json", "base-blob"));
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(File("old.json", "base-blob"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ABaseChangeToTheNewNameOfAFileRenovateRenamed_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[RenovateChange] = Ahead(File("new.json", "r1", "renamed", "old.json"));
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(File("new.json", "base-blob"));
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(File("new.json", "base-blob"));

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ARenovateChangeTheBaseDoesNotTouch_IsAccepted()
    {
        var github = HonestMerge();
        github.Comparisons[RenovateChange] = Ahead(File("package.json", "r1"), File("package-lock.json", "r2"));

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task ARenovateChangeThatGitHubDoesNotKnow_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons.Remove(RenovateChange);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ARenovateChangeWithoutAFileList_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[RenovateChange] = new GitHubComparison("ahead", null, null);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ATruncatedRenovateChange_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[RenovateChange] = Ahead([.. Enumerable.Range(0, GitHubApiClient.CompareFileLimit).Select(i => File($"r{i}", $"s{i}"))]);

        Assert.False(await Check(github));
    }

    [Theory]
    [InlineData("added-side")]
    [InlineData("base-side")]
    public async Task AComparisonWithoutAFileList_IsRejected(string side)
    {
        var github = HonestMerge();
        var key = side == "added-side" ? $"{RenovateSha}...{MergeSha}" : $"{BranchPoint}...{BaseParent}";
        github.Comparisons[key] = new GitHubComparison("ahead", null, null);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AnEmptyBaseRef_IsRejectedWithoutReadingAnything()
    {
        var github = HonestMerge();
        github.PullRequest = github.PullRequest with { BaseRef = string.Empty };
        github.Calls.Clear();

        Assert.False(await Check(github));
        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task APrWhoseBaseIsNotTheDefaultBranch_IsRejected()
    {
        var github = HonestMerge();
        github.DefaultBranch = "develop";

        Assert.False(await Check(github));
        Assert.DoesNotContain(github.Calls, c => c.StartsWith("compare:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APrWhoseBaseIsTheDefaultBranchByAnotherCase_IsRejected()
    {
        var github = HonestMerge();
        github.DefaultBranch = "Main";

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AnUnreadableDefaultBranch_IsRejected()
    {
        var github = HonestMerge();
        github.DefaultBranch = string.Empty;

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AnAllRenovatePr_DoesNotReadTheDefaultBranch()
    {
        var github = new FakeGitHubApi();

        Assert.True(await Check(github));
        Assert.DoesNotContain("default-branch", github.Calls);
    }

    [Fact]
    public async Task ATransientErrorReadingTheDefaultBranch_Throws()
    {
        var github = HonestMerge();
        github.Failure = new GitHubApiException("GitHub repository read failed.", HttpStatusCode.ServiceUnavailable);

        var thrown = await Assert.ThrowsAsync<GitHubApiException>(() => Check(github));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, thrown.StatusCode);
    }

    [Fact]
    public async Task ARenovateCommitOnTopOfAMerge_IsRejected()
    {
        var github = HonestMerge();
        github.Commits.Add(Renovate("8888888888888888888888888888888888888888", MergeSha));
        github.PullRequest = github.PullRequest with { HeadSha = "8888888888888888888888888888888888888888", Commits = 3 };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AnotherAuthorsCommitOnTopOfAMerge_IsRejected()
    {
        var github = HonestMerge();
        github.Commits.Add(FakeGitHubApi.RenovateCommit("8888888888888888888888888888888888888888") with { AuthorLogin = "alice", AuthorId = 5, Parents = [MergeSha] });
        github.PullRequest = github.PullRequest with { HeadSha = "8888888888888888888888888888888888888888", Commits = 3 };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ANonRenovateCommitFirst_IsRejected()
    {
        var github = HonestMerge();
        github.Commits[0] = github.Commits[0] with { AuthorLogin = "alice", AuthorId = 5 };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ARenovateCommitBelowAMerge_ThatIsNotChainedToItsPredecessor_IsRejected()
    {
        var github = HonestMerge();
        github.Commits[1] = Merge(MergeSha, "9999999999999999999999999999999999999999", BaseParent);

        Assert.False(await Check(github));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ACommitAfterRenovatesThatIsNotATwoParentMerge_IsRejected(int parents)
    {
        var github = HonestMerge();
        github.Commits[1] = github.Commits[1] with { Parents = [.. new[] { RenovateSha, BaseParent, SecondBaseParent }.Take(parents)] };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ACommitWithUnknownParents_IsRejected()
    {
        var github = HonestMerge();
        github.Commits[1] = github.Commits[1] with { Parents = null };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ARenovateCommitThatIsItselfAMerge_IsRejected()
    {
        var github = HonestMerge();
        github.Commits[0] = github.Commits[0] with { Parents = [RenovateParent, BaseParent] };

        Assert.False(await Check(github));
    }

    [Theory]
    [InlineData("ahead")]
    [InlineData("diverged")]
    public async Task ASecondParentThatIsNotOnTheBase_IsRejected(string status)
    {
        var github = HonestMerge();
        github.Comparisons[$"main...{BaseParent}"] = new GitHubComparison(status, null, []);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ASecondParentThatIsTheBaseTip_IsAccepted()
    {
        var github = HonestMerge();
        github.Comparisons[$"main...{BaseParent}"] = new GitHubComparison("identical", null, []);

        Assert.True(await Check(github));
    }

    [Fact]
    public async Task ALastCommitThatIsNotTheJudgedHead_IsRejected()
    {
        var github = HonestMerge();
        github.PullRequest = github.PullRequest with { HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ACommitCountThatDiffersFromTheListedCommits_IsRejected()
    {
        var github = HonestMerge();
        github.PullRequest = github.PullRequest with { Commits = 3 };

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task MoreMergesThanTheLimit_AreRejectedWithoutReadingComparisons()
    {
        var github = HonestMerge();
        var previous = MergeSha;
        for (var i = 0; i < RenovateCommitCheck.MaxMerges; i++)
        {
            var sha = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(40, 'c');
            github.Commits.Add(Merge(sha, previous, BaseParent));
            previous = sha;
        }

        github.PullRequest = github.PullRequest with { HeadSha = previous, Commits = github.Commits.Count };
        github.Calls.Clear();

        Assert.False(await Check(github));
        Assert.Empty(github.Calls);
    }

    [Theory]
    [InlineData("added-side")]
    [InlineData("base-side")]
    public async Task ATruncatedFileList_IsRejected(string side)
    {
        var github = HonestMerge();
        var files = Enumerable.Range(0, GitHubApiClient.CompareFileLimit).Select(i => File($"f{i}", $"s{i}")).ToArray();
        if (side == "added-side")
        {
            github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(files);
            github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(files);
        }
        else
        {
            github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(files);
            github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(files);
        }

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task OneFileUnderTheLimit_IsAccepted()
    {
        var github = HonestMerge();
        var files = Enumerable.Range(0, GitHubApiClient.CompareFileLimit - 1).Select(i => File($"f{i}", $"s{i}")).ToArray();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(files);
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead([.. files.Reverse()]);

        Assert.True(await Check(github));
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("copied")]
    public async Task ARenamedOrCopiedFile_IsRejected(string status)
    {
        var github = HonestMerge();
        var moved = File("moved.txt", "ddd", status, "old.txt");
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(moved);
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(moved);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task AFileWithoutABlobSha_IsRejected()
    {
        var github = HonestMerge();
        var blank = new GitHubCompareFile("README.md", "modified", null);
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = Ahead(blank);
        github.Comparisons[$"{BranchPoint}...{BaseParent}"] = Ahead(blank);

        Assert.False(await Check(github));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AComparisonGitHubDoesNotKnow_IsRejected(int missing)
    {
        var github = HonestMerge();
        string[] keys = [$"main...{BaseParent}", $"main...{RenovateSha}", $"{RenovateSha}...{MergeSha}", $"{BranchPoint}...{BaseParent}"];
        github.Comparisons.Remove(keys[missing]);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ABranchPointGitHubDoesNotReport_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"main...{RenovateSha}"] = new GitHubComparison("ahead", null, []);

        Assert.False(await Check(github));
    }

    [Fact]
    public async Task ARenovateCommitThatIsNotAheadOfTheMerge_IsRejected()
    {
        var github = HonestMerge();
        github.Comparisons[$"{RenovateSha}...{MergeSha}"] = new GitHubComparison("diverged", null, [File("README.md", "aaa"), File("src/new.txt", "bbb", "added")]);

        Assert.False(await Check(github));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ATransientGitHubError_ThrowsInsteadOfGivingAVerdict(HttpStatusCode status)
    {
        var github = HonestMerge();
        github.CompareFailure = new GitHubApiException("GitHub comparison read failed.", status);

        var thrown = await Assert.ThrowsAsync<GitHubApiException>(() => Check(github));

        Assert.Equal(status, thrown.StatusCode);
    }

    [Fact]
    public async Task AFailureThatStrikesAfterTheFirstComparison_StillThrows()
    {
        var github = HonestMerge();
        github.CompareFailureAfter = 2;
        github.CompareFailure = new GitHubApiException("GitHub comparison read failed.", HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<GitHubApiException>(() => Check(github));
    }

    [Fact]
    public void TheStatedCommit_IsTheLastRenovateCommitInTheLeadingRun()
    {
        var github = HonestMerge();

        Assert.Equal(RenovateSha, TriageRules.StatedCommit([.. github.Commits])!.Sha);
    }

    [Fact]
    public void TheStatedCommit_OfAnAllRenovatePr_IsTheHead()
    {
        var github = new FakeGitHubApi();
        github.Commits.Add(FakeGitHubApi.RenovateCommit("8888888888888888888888888888888888888888"));

        Assert.Equal("8888888888888888888888888888888888888888", TriageRules.StatedCommit([.. github.Commits])!.Sha);
    }

    [Fact]
    public void TheStatedCommit_IsNotPickedByDate()
    {
        var github = HonestMerge();
        github.Commits.Add(FakeGitHubApi.RenovateCommit("8888888888888888888888888888888888888888", "chore(deps): update newer to v9\n\n| datasource | package | from | to |\n| --- | --- | --- | --- |\n| npm | newer | 8.0.0 | 9.0.0 |"));

        Assert.Equal(RenovateSha, TriageRules.StatedCommit([.. github.Commits])!.Sha);
    }

    [Fact]
    public void WithoutALeadingRenovateCommit_ThereIsNoStatedCommit()
    {
        var github = HonestMerge();
        github.Commits.Reverse();

        Assert.Null(TriageRules.StatedCommit([.. github.Commits]));
        Assert.Null(TriageRules.StatedCommit([]));
    }
}
