using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Tests.GitHub;

public class GitHubRepoNameTests
{
    [Theory]
    [InlineData("octo/widgets", "octo", "widgets")]
    [InlineData("a-b_c/d.e-f", "a-b_c", "d.e-f")]
    public void AnOwnerAndNameAreSplit(string fullName, string owner, string name)
    {
        Assert.True(GitHubRepoName.TryParse(fullName, out var parsedOwner, out var parsedName));
        Assert.Equal((owner, name), (parsedOwner, parsedName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("octo")]
    [InlineData("octo/")]
    [InlineData("/widgets")]
    [InlineData("octo/widgets/extra")]
    [InlineData("./widgets")]
    [InlineData("octo/..")]
    [InlineData("octo/wid gets")]
    [InlineData("octo/wid?gets")]
    [InlineData("octo\\widgets")]
    public void AnythingElseIsRejected(string? fullName)
    {
        Assert.False(GitHubRepoName.TryParse(fullName, out var owner, out var name));
        Assert.Empty(owner);
        Assert.Empty(name);
    }
}
