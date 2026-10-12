using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class PrDiffTests
{
    private static GitHubPullRequestFile File(string name, string? patch = "@@ -1 +1 @@\n-old\n+new") => new(name, patch);

    [Fact]
    public void EachFileIsListedWithItsPatch_InTheOrderGitHubReturnsThem()
    {
        var diff = PrDiff.Build([File("a.txt", "@@\n-1\n+2"), File("b.txt", "@@\n-3\n+4")], 1024);

        Assert.Equal("### a.txt\n@@\n-1\n+2\n\n### b.txt\n@@\n-3\n+4\n\n", diff);
    }

    [Fact]
    public void AFileWithoutAPatch_IsListedByNameOnly()
    {
        var diff = PrDiff.Build([File("logo.png", null), File("huge.json", "")], 1024);

        Assert.Equal("### logo.png (no patch)\n\n### huge.json (no patch)\n\n", diff);
    }

    [Fact]
    public void NoFiles_GiveAnEmptyDiff()
    {
        Assert.Equal(string.Empty, PrDiff.Build([], 1024));
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/pnpm-lock.yaml")]
    [InlineData("yarn.lock")]
    [InlineData("go.sum")]
    [InlineData("uv.lock")]
    [InlineData("Cargo.lock")]
    [InlineData("Gemfile.lock")]
    [InlineData("poetry.lock")]
    [InlineData("packages.lock.json")]
    [InlineData("deep/dir/Package-Lock.json")]
    [InlineData(".terraform.lock.hcl")]
    [InlineData("bun.lockb")]
    public void LockFiles_AreListedAfterEverythingElse(string lockFile)
    {
        var diff = PrDiff.Build([File(lockFile), File("src/app.cs"), File("README.md")], 1024);

        Assert.Equal(["### src/app.cs", "### README.md", $"### {lockFile}"], diff.Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)));
    }

    [Fact]
    public void FilesThatMerelyMentionLock_AreNotLockFiles()
    {
        var diff = PrDiff.Build([File("lockdown.md"), File("a.txt")], 1024);

        Assert.Equal(["### lockdown.md", "### a.txt"], diff.Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)));
    }

    [Fact]
    public void FilesAfterTheCapIsReached_AreLeftOut()
    {
        var files = Enumerable.Range(0, 1000).Select(i => File($"f{i}.txt", new string('x', 100))).ToList();

        var diff = PrDiff.Build(files, 2048);

        Assert.InRange(Encoding.UTF8.GetByteCount(diff), 2048, 2048 + 200);
        Assert.DoesNotContain("f999.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullFileList_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PrDiff.Build(null!, 1024));
    }
}
