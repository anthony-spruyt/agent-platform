using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.GitHub;

public class GitHubLabelGroupsTests
{
    private static readonly string[] Group = ["owned/a", "owned/b", "owned/c"];

    private static FakeGitHubApi With(params string[] labels) =>
        new() { PullRequest = FakeGitHubApi.Renovate() with { Labels = labels } };

    private static Task Set(FakeGitHubApi github, string? label) =>
        github.SetExclusiveLabelAsync("anthony-spruyt/widgets", 7, Group, label, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RemovesTheOtherGroupLabelsThatArePresent_ThenAddsTheNewOne()
    {
        var github = With("owned/a", "owned/b");

        await Set(github, "owned/c");

        Assert.Equal(["owned/a", "owned/b"], github.RemovedLabels);
        Assert.Equal(["owned/c"], github.AddedLabels);
    }

    [Fact]
    public async Task LeavesLabelsOutsideTheGroupAlone()
    {
        var github = With("owned/a", "blocked", "agent/needs-human-review");

        await Set(github, "owned/b");

        Assert.Equal(["owned/a"], github.RemovedLabels);
        Assert.Contains("blocked", github.PullRequest.Labels);
        Assert.Contains("agent/needs-human-review", github.PullRequest.Labels);
    }

    [Fact]
    public async Task NeverRemovesAGroupLabelThatIsAbsent()
    {
        var github = With("owned/a");

        await Set(github, "owned/a");

        Assert.Empty(github.RemovedLabels);
    }

    [Fact]
    public async Task AlabelAlreadyOnTheItemIsNotRemovedAndReAdded()
    {
        var github = With("owned/a", "owned/b");

        await Set(github, "owned/a");

        Assert.Equal(["owned/b"], github.RemovedLabels);
        Assert.DoesNotContain("label-remove:owned/a", github.Writes);
    }

    [Fact]
    public async Task ANullLabel_OnlyClearsTheGroup()
    {
        var github = With("owned/a", "blocked");

        await Set(github, null);

        Assert.Equal(["owned/a"], github.RemovedLabels);
        Assert.Empty(github.AddedLabels);
    }
}
