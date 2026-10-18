using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.PullRequests;

public class StatedVersionsTests
{
    [Fact]
    public void TheTableRows_AreReadByHeaderName()
    {
        const string message = "chore(deps): update x\n\n| package | to | from | datasource |\n|---|---|---|---|\n| actions/checkout | 4.2.0 | 4.1.0 | github-tags |\n| p | 2 | 1 | docker |";

        var stated = DiffShape.ReadStatedVersions(message);

        Assert.NotNull(stated);
        Assert.Equal([new StatedVersion("actions/checkout", "4.1.0", "4.2.0", "github-tags"), new StatedVersion("p", "1", "2", "docker")], stated.Rows);
    }

    [Theory]
    [InlineData("chore(deps): update x to 8.22.0", "8.22.0")]
    [InlineData("chore(deps): update x to v0.60.0 [security]", "v0.60.0")]
    [InlineData("chore(deps): update x to 3.14-slim", "3.14-slim")]
    [InlineData("chore(deps): update x", null)]
    public void WithNoTable_OnlyTheSubjectVersionIsStated(string subject, string? version)
    {
        var stated = DiffShape.ReadStatedVersions(subject);

        Assert.NotNull(stated);
        Assert.Null(stated.Rows);
        Assert.Equal(version, stated.SubjectVersion);
    }

    [Theory]
    [InlineData("| package | to |\n|-|-|\n| p | 1.1.0 |")]
    [InlineData("| datasource | package | from | to |\n| - | - | - | - |\n| docker | p | 1.0.0 |")]
    public void AMalformedTable_ReadsAsNothing(string table)
    {
        Assert.Null(DiffShape.ReadStatedVersions("chore(deps): update x\n\n" + table));
    }
}
