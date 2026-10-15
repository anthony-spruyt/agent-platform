using System.Text.Json;
using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.PullRequests;

public class DiffShapeFixtureTests
{
    private sealed record FixtureFile(string Path, string Status, string? Patch);

    private sealed record FixtureCommit(string Sha, string Message);

    private sealed record Fixture(int ExpectedFileCount, string Head, List<FixtureFile> Files, List<FixtureCommit> Commits);

    private static DiffShapeResult Classify(string name)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", $"diffshape-{name}.json"));
        var fixture = JsonSerializer.Deserialize<Fixture>(json, JsonSerializerOptions.Web)!;
        return DiffShape.Classify(
            [.. fixture.Files.Select(f => new DiffFile(f.Path, f.Status, f.Patch))],
            fixture.ExpectedFileCount,
            fixture.Head,
            [.. fixture.Commits.Select(c => new DiffCommit(c.Sha, c.Message))],
            fixture.Head,
            fixture.Commits[^1].Message);
    }

    [Theory]
    [InlineData("Chromance-11")]
    [InlineData("SunGather-410")]
    [InlineData("container-images-2206")]
    [InlineData("container-images-2288")]
    [InlineData("spruyt-labs-3405")]
    [InlineData("spruyt-labs-3414")]
    [InlineData("spruyt-labs-3426")]
    [InlineData("spruyt-labs-3465")]
    [InlineData("xfg-1133")]
    public void MergedRenovatePrs_Pass(string name)
    {
        Assert.True(Classify(name).Passed);
    }

    [Theory]
    [InlineData("container-images-2291", DiffShapeFailure.VersionNotStated)]
    [InlineData("Chromance-6", DiffShapeFailure.VersionNotStated)]
    [InlineData("spruyt-labs-3502", DiffShapeFailure.VersionNotStated)]
    [InlineData("container-images-2241", DiffShapeFailure.UnpairedLines)]
    [InlineData("container-images-2285", DiffShapeFailure.UnpairedLines)]
    public void MergedRenovatePrsWithUnstatedOrUnpairedChanges_Fail(string name, DiffShapeFailure expected)
    {
        Assert.Equal(expected, Classify(name).Failure);
    }
}
