using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapMergeGateTests
{
    private const string Fork = "octo/livemap";

    private const string Branch = "livemap/build-400";

    private static readonly string Head = new('a', 40);

    private static readonly string Main = new('b', 40);

    private const long CiSuite = 7000;

    private static readonly string[] _required = ["lint / MegaLinter", "repo / Build, test, package"];

    private static LivemapGateFacts Good() => new(
        Pull: new LivemapPullRequest(7, "open", false, false, "main", Fork, Branch, Fork, Head, "skynet-rw[bot]", "Bot", 1),
        Fork: Fork,
        Branch: Branch,
        ExpectedHeadSha: Head,
        AppLogin: "skynet-rw[bot]",
        MainHead: Main,
        HeadParents: [Main],
        Files: [new LivemapPullRequestFile("data/builds.json", "modified", null)],
        HeadFileMode: "100644",
        MainFileMode: "100644",
        CheckRuns: [.. _required.Select(name => Run(name))],
        CiSuiteIds: new HashSet<long> { CiSuite });

    private static LivemapCheckRun Run(string name, string status = "completed", string? conclusion = "success", string? app = "github-actions", long suite = CiSuite) =>
        new(name, status, status == "completed" ? conclusion : null, app, suite);

    private static LivemapGateVerdict Evaluate(LivemapGateFacts facts, params string[] required) =>
        LivemapMergeGate.Evaluate(facts, required.Length == 0 ? _required : required);

    [Fact]
    public void AnUntouchedPullRequestWithEveryRequiredCheckGreen_Passes()
    {
        Assert.Equal(LivemapGateOutcome.Passed, Evaluate(Good()).Outcome);
    }

    public static TheoryData<string, Func<LivemapGateFacts, LivemapGateFacts>> Refusals => new()
    {
        { "closed", f => f with { Pull = f.Pull with { State = "closed" } } },
        { "merged", f => f with { Pull = f.Pull with { Merged = true } } },
        { "draft", f => f with { Pull = f.Pull with { Draft = true } } },
        { "head repo is another", f => f with { Pull = f.Pull with { HeadRepo = "octo/other" } } },
        { "head repo is gone", f => f with { Pull = f.Pull with { HeadRepo = string.Empty } } },
        { "base repo is another", f => f with { Pull = f.Pull with { BaseRepo = "octo/other" } } },
        { "base is not main", f => f with { Pull = f.Pull with { BaseRef = "release" } } },
        { "head branch is another", f => f with { Pull = f.Pull with { HeadRef = "livemap/build-401" } } },
        { "author is a person", f => f with { Pull = f.Pull with { AuthorLogin = "someone", AuthorType = "User" } } },
        { "author is another bot", f => f with { Pull = f.Pull with { AuthorLogin = "other[bot]" } } },
        { "author named like the app but a user", f => f with { Pull = f.Pull with { AuthorType = "User" } } },
        { "head is not the committed one", f => f with { Pull = f.Pull with { HeadSha = new string('c', 40) } } },
        { "more than one commit", f => f with { Pull = f.Pull with { Commits = 2 } } },
        { "the commit has two parents", f => f with { HeadParents = [Main, new string('d', 40)] } },
        { "the commit has no parent", f => f with { HeadParents = [] } },
        { "no files", f => f with { Files = [] } },
        { "another file too", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("README.md", "modified", null)] } },
        { "another file only", f => f with { Files = [new LivemapPullRequestFile("data/other.json", "modified", null)] } },
        { "the file is added", f => f with { Files = [new LivemapPullRequestFile("data/builds.json", "added", null)] } },
        { "the file is removed", f => f with { Files = [new LivemapPullRequestFile("data/builds.json", "removed", null)] } },
        { "the file is renamed in", f => f with { Files = [new LivemapPullRequestFile("data/builds.json", "modified", "data/old.json")] } },
        { "the file mode changed on the head", f => f with { HeadFileMode = "100755" } },
        { "the file is a link on the head", f => f with { HeadFileMode = "120000" } },
        { "the file is missing on the head", f => f with { HeadFileMode = null } },
        { "the file mode on main is not plain", f => f with { MainFileMode = "100755" } },
        { "the file is missing on main", f => f with { MainFileMode = null } },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void APullRequestThatIsNotTheOneTheWorkerOpened_IsRefused(string why, Func<LivemapGateFacts, LivemapGateFacts> change)
    {
        var verdict = Evaluate(change(Good()));

        Assert.True(verdict.Outcome == LivemapGateOutcome.Refused, why);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void ARepositoryNameIsComparedWithoutRegardToCase()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { Pull = facts.Pull with { HeadRepo = "OCTO/Livemap", BaseRepo = "Octo/LIVEMAP" } });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void WhenMainHasMovedOnFromTheParent_TheVerdictIsMainMoved_EvenWithEveryCheckGreen()
    {
        var verdict = Evaluate(Good() with { MainHead = new string('e', 40) });

        Assert.Equal(LivemapGateOutcome.MainMoved, verdict.Outcome);
    }

    [Fact]
    public void ARefusalOutranksAMovedMain()
    {
        var facts = Good();

        var verdict = Evaluate(facts with { MainHead = new string('e', 40), Files = [] });

        Assert.Equal(LivemapGateOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void ARequiredCheckThatHasNotAppeared_IsPending_AndNamed()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter")] });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public void ARequiredCheckThatHasNotFinished_IsPending(string status)
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", status)] });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("neutral")]
    [InlineData("stale")]
    [InlineData("skipped")]
    public void ARequiredCheckThatFinishedWithAnythingButSuccess_Fails_AndIsNamed(string conclusion)
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", conclusion: conclusion)] });

        Assert.Equal(LivemapGateOutcome.Failed, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Fact]
    public void AFailedCheckOutranksAPendingOne()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / MegaLinter", conclusion: "failure")] });

        Assert.Equal(LivemapGateOutcome.Failed, verdict.Outcome);
        Assert.Equal(["lint / MegaLinter"], verdict.Names);
    }

    [Fact]
    public void EveryFailedCheckIsNamed_InTheOrderOfTheRequiredList()
    {
        var verdict = Evaluate(Good() with
        {
            CheckRuns = [Run("repo / Build, test, package", conclusion: "failure"), Run("lint / MegaLinter", conclusion: "cancelled")],
        });

        Assert.Equal(["lint / MegaLinter", "repo / Build, test, package"], verdict.Names);
    }

    [Fact]
    public void ACheckFromAnotherApp_NeverCountsForARequiredName()
    {
        var verdict = Evaluate(Good() with
        {
            CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", app: "some-other-app")],
        });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GitHub-Actions")]
    [InlineData("github-actions-fork")]
    public void ACheckWhoseAppIsNotGitHubActions_NeverCounts(string? app)
    {
        var verdict = Evaluate(Good() with { CheckRuns = [.. _required.Select(name => Run(name, app: app))] });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
    }

    [Fact]
    public void ACheckFromAnotherApp_CannotFailARequiredName_WhenTheActionsOneSucceeded()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [.. Good().CheckRuns, Run("lint / MegaLinter", conclusion: "failure", app: "some-other-app")] });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void ARequiredNameInASuiteThatIsNotTheForksCiRun_NeverCounts()
    {
        var verdict = Evaluate(Good() with
        {
            CheckRuns = [Run("lint / MegaLinter"), Run("repo / Build, test, package", suite: CiSuite + 1)],
        });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(["repo / Build, test, package"], verdict.Names);
    }

    [Fact]
    public void ARequiredNameInASuiteThatIsNotTheForksCiRun_CannotFailTheOneInTheCiRun()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [.. Good().CheckRuns, Run("lint / MegaLinter", conclusion: "failure", suite: CiSuite + 1)] });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void WithNoCiRunOfTheForkForTheHead_EveryRequiredCheckIsPending()
    {
        var verdict = Evaluate(Good() with { CiSuiteIds = new HashSet<long>() });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(_required, verdict.Names);
    }

    [Fact]
    public void ACheckThatIsNotRequired_IsIgnored_WhateverItConcluded()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [.. Good().CheckRuns, Run("docs / Spelling", conclusion: "failure"), Run("summary / Check Results", status: "queued")] });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void ARequiredNameWithSeveralActionsRuns_NeedsEveryOneOfThemToSucceed()
    {
        var runs = new[] { Run("lint / MegaLinter"), Run("lint / MegaLinter", conclusion: "failure"), Run("repo / Build, test, package") };

        var verdict = Evaluate(Good() with { CheckRuns = runs });

        Assert.Equal(LivemapGateOutcome.Failed, verdict.Outcome);
        Assert.Equal(["lint / MegaLinter"], verdict.Names);
    }

    [Fact]
    public void CheckNamesAreMatchedExactly()
    {
        var verdict = Evaluate(Good() with { CheckRuns = [Run("lint / megalinter"), Run("repo / Build, test, package ")] });

        Assert.Equal(LivemapGateOutcome.Pending, verdict.Outcome);
        Assert.Equal(_required, verdict.Names);
    }

    [Fact]
    public void WithNoRequiredChecksAtAll_NothingPasses()
    {
        var verdict = LivemapMergeGate.Evaluate(Good(), []);

        Assert.Equal(LivemapGateOutcome.Refused, verdict.Outcome);
    }
}
