using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPinGateTests
{
    private const string PinRepo = "octo/cluster";

    private const string PinPath = "apps/livemap/pin.yaml";

    private const string App = "skynet-rw[bot]";

    private const string Branch = "livemap/pin-v2.0.0";

    private static readonly string Head = new('a', 40);

    private static readonly string Main = new('b', 40);

    private static LivemapPinFacts Good() => new(
        Pull: new LivemapPullRequest(88, "open", false, false, "main", PinRepo, Branch, PinRepo, Head, App, "Bot", 1, "chore(enshrouded): update livemap to v2.0.0"),
        PinRepo: PinRepo,
        PinPath: PinPath,
        Branch: Branch,
        ExpectedHeadSha: Head,
        AppLogin: App,
        MainHead: Main,
        HeadParents: [Main],
        Files: [new LivemapPullRequestFile(PinPath, "modified", null, "@@ -1 +1 @@")]);

    public static TheoryData<string, Func<LivemapPinFacts, LivemapPinFacts>> Refusals => new()
    {
        { "closed", f => f with { Pull = f.Pull with { State = "closed" } } },
        { "merged", f => f with { Pull = f.Pull with { Merged = true } } },
        { "draft", f => f with { Pull = f.Pull with { Draft = true } } },
        { "head repo", f => f with { Pull = f.Pull with { HeadRepo = "someone/cluster" } } },
        { "base repo", f => f with { Pull = f.Pull with { BaseRepo = "someone/cluster" } } },
        { "base branch", f => f with { Pull = f.Pull with { BaseRef = "release" } } },
        { "head branch", f => f with { Pull = f.Pull with { HeadRef = "livemap/pin-v9.9.9" } } },
        { "author", f => f with { Pull = f.Pull with { AuthorLogin = "someone[bot]" } } },
        { "author type", f => f with { Pull = f.Pull with { AuthorType = "User" } } },
        { "commit count", f => f with { Pull = f.Pull with { Commits = 2 } } },
        { "parent count", f => f with { HeadParents = [Main, Head] } },
        { "head moved", f => f with { Pull = f.Pull with { HeadSha = new string('c', 40) } } },
        { "no file", f => f with { Files = [] } },
        { "other file", f => f with { Files = [new LivemapPullRequestFile("cluster/apps/other.yaml", "modified", null, "@@")] } },
        { "extra file", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("README.md", "modified", null, "@@")] } },
        { "encrypted file beside the pin", f => f with { Files = [.. f.Files, new LivemapPullRequestFile("apps/livemap/secrets.sops.yaml", "modified", null, "@@")] } },
        { "pin file in another folder", f => f with { Files = [new LivemapPullRequestFile("other/" + PinPath, "modified", null, "@@")] } },
        { "pin path in another case", f => f with { Files = [new LivemapPullRequestFile(PinPath.ToUpperInvariant(), "modified", null, "@@")] } },
        { "pin file added", f => f with { Files = [new LivemapPullRequestFile(PinPath, "added", null, "@@")] } },
        { "pin file removed", f => f with { Files = [new LivemapPullRequestFile(PinPath, "removed", null, "@@")] } },
        { "pin file renamed in", f => f with { Files = [new LivemapPullRequestFile(PinPath, "renamed", "apps/livemap/old.yaml", "@@")] } },
        { "pin file modified from another path", f => f with { Files = [new LivemapPullRequestFile(PinPath, "modified", "apps/livemap/old.yaml", "@@")] } },
    };

    [Fact]
    public void ThePinPullRequestTheWorkerMade_Passes()
    {
        Assert.Equal(LivemapGateOutcome.Passed, LivemapPinGate.Evaluate(Good()).Outcome);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AnythingElseThanThePinFile_OrThePullRequestTheWorkerMade_IsRefused(string point, Func<LivemapPinFacts, LivemapPinFacts> change)
    {
        var verdict = LivemapPinGate.Evaluate(change(Good()));

        Assert.True(verdict.Outcome == LivemapGateOutcome.Refused, point);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason), point);
    }

    [Fact]
    public void WhenMainHasMovedOn_ThePinIsMadeAgain()
    {
        var verdict = LivemapPinGate.Evaluate(Good() with { HeadParents = [new string('c', 40)] });

        Assert.Equal(LivemapGateOutcome.MainMoved, verdict.Outcome);
    }

    [Fact]
    public void ARefusal_IsNotHiddenByMainMoving()
    {
        var facts = Good();

        var verdict = LivemapPinGate.Evaluate(facts with { HeadParents = [new string('c', 40)], Files = [.. facts.Files, new LivemapPullRequestFile("README.md", "modified", null, "@@")] });

        Assert.Equal(LivemapGateOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void TheRepositoryAndLoginAreComparedWithoutRegardToCase()
    {
        var facts = Good();

        var verdict = LivemapPinGate.Evaluate(facts with { Pull = facts.Pull with { HeadRepo = "OCTO/Cluster", BaseRepo = "Octo/CLUSTER", AuthorLogin = "Skynet-RW[bot]" } });

        Assert.Equal(LivemapGateOutcome.Passed, verdict.Outcome);
    }

    [Fact]
    public void ThePinBranchIsNamedAfterTheRelease_AndOnlyAPlainReleaseTagMakesOne()
    {
        Assert.Equal("livemap/pin-v2.0.0", LivemapPinBranch.For("v2.0.0"));
        Assert.True(LivemapPinBranch.IsMatch(LivemapPinBranch.For("v12.34.56")));
        Assert.Throws<ArgumentException>(() => LivemapPinBranch.For("v2.0.0-rc1"));
        Assert.Throws<ArgumentException>(() => LivemapPinBranch.For("main"));
    }
}
