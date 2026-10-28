using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.RenovateTriage;

public class ChangelogFetcherTests
{
    private const string Repo = "anthony-spruyt/widgets";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RenovateUpdate Update(string dep = "dep", string? source = "upstream/dep", string to = "v1.2.3") =>
        new(dep, source, "v1.2.2", to);

    private static (ChangelogFetcher Fetcher, FakeGitHubApi Api) Create()
    {
        var api = new FakeGitHubApi();
        return (new ChangelogFetcher(api), api);
    }

    [Fact]
    public async Task Fetch_UsesTheReleaseNotesOfTheExactTag()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", "Dep 1.2.3", "- fixed the crash");

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update()], Ct));

        Assert.Equal(new ChangelogSection("dep", new ChangelogSource("upstream/dep", "v1.2.3", ChangelogKind.Release), "- fixed the crash"), section);
    }

    [Fact]
    public async Task Fetch_NamesTheSourceRepoAndTagOfTheReleaseItUsed()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:1.2.3"] = new GitHubRelease("1.2.3", null, "notes");

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update(to: "1.2.3")], Ct));

        Assert.Equal(new ChangelogSource("upstream/dep", "1.2.3", ChangelogKind.Release), section.Source);
    }

    [Fact]
    public async Task Fetch_NamesTheSourceRepoAndTagOfTheChangelogFileItUsed()
    {
        var (fetcher, api) = Create();
        api.FileContents["upstream/dep:CHANGELOG.md@1.2.3"] = "## 1.2.3\n\n- fixed";

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update(to: "1.2.3")], Ct));

        Assert.Equal(new ChangelogSource("upstream/dep", "1.2.3", ChangelogKind.File), section.Source);
    }

    [Fact]
    public async Task Fetch_TriesTheTagFormsInOrder()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:1.2.3"] = new GitHubRelease("1.2.3", null, "notes");

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update(to: "1.2.3")], Ct));

        Assert.Equal("release 1.2.3", section.Origin);
        Assert.Equal(["release:upstream/dep:v1.2.3", "release:upstream/dep:1.2.3"], api.Calls);
    }

    [Fact]
    public async Task Fetch_TriesThePackageScopedTagLast()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:@scope/dep@1.2.3"] = new GitHubRelease("@scope/dep@1.2.3", null, "scoped notes");

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update(dep: "@scope/dep")], Ct));

        Assert.Equal("scoped notes", section.Text);
        Assert.Equal(["release:upstream/dep:v1.2.3", "release:upstream/dep:1.2.3", "release:upstream/dep:@scope/dep@1.2.3"], api.Calls);
    }

    [Fact]
    public async Task Fetch_SkipsAReleaseWithAnEmptyBodyAndFallsBackToTheChangelogFileAtTheTag()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", "x", "  ");
        api.FileContents["upstream/dep:CHANGELOG.md@v1.2.3"] = "# Changelog\n\n## 1.2.3\n\n- fixed\n\n## 1.2.2\n\n- old";

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update()], Ct));

        Assert.Equal("changelog CHANGELOG.md at v1.2.3", section.Origin);
        Assert.Equal("## 1.2.3\n\n- fixed", section.Text);
    }

    [Fact]
    public async Task Fetch_WhenTheChangelogHasNoSectionForTheVersion_FindsNothing()
    {
        var (fetcher, api) = Create();
        api.FileContents["upstream/dep:CHANGELOG.md@v1.2.3"] = "## 1.0.0\n- old";

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update()], Ct));
    }

    [Fact]
    public async Task Fetch_WhenNothingIsFound_ReturnsNothing()
    {
        var (fetcher, _) = Create();

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update()], Ct));
    }

    [Fact]
    public async Task Fetch_SkipsAnUpdateWithoutAGitHubSource()
    {
        var (fetcher, api) = Create();

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update(source: null)], Ct));

        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData("upstream")]
    [InlineData("upstream/dep/extra")]
    [InlineData("../x")]
    [InlineData("up stream/dep")]
    public async Task Fetch_SkipsASourceThatIsNotOwnerSlashName(string source)
    {
        var (fetcher, api) = Create();

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update(source: source)], Ct));

        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData("1.2.3?x=1")]
    [InlineData("../1.2.3")]
    [InlineData("1.2.3 beta")]
    public async Task Fetch_NeverRequestsATagThatIsNotPlain(string to)
    {
        var (fetcher, api) = Create();

        await fetcher.FetchAsync(Repo, [Update(to: to)], Ct);

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Fetch_ReadsNothingForARangeTheTableParserKeeps()
    {
        var (fetcher, api) = Create();
        const string Body = "| Package | Change |\n|---|---|\n| [tfe](https://registry.terraform.io/providers/hashicorp/tfe) ([source](https://redirect.github.com/hashicorp/terraform-provider-tfe)) | `~> 0.76` → `~> 0.78` |\n";

        var sections = await fetcher.FetchAsync(Repo, RenovateBodyTable.Parse(Body), Ct);

        Assert.Empty(sections);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData("https://redirect.github.com/upstream/dep", true)]
    [InlineData("https://redirect.github.com/upstream/dep/", true)]
    [InlineData("https://redirect.github.com/upstream/dep/tree/HEAD/packages/x", false)]
    [InlineData("https://redirect.github.com/upstream/dep/tree/main/packages/x/", false)]
    [InlineData("https://redirect.github.com/upstream/dep/blob/main/CHANGELOG.md", false)]
    public async Task Fetch_ReadsASourceOnlyWhenItsLinkIsTheRepoRoot(string source, bool fetched)
    {
        var (fetcher, api) = Create();
        var body = $"| Package | Change |\n|---|---|\n| [dep](https://example.test) ([source]({source})) | `v1.2.2` → `v1.2.3` |\n";

        await fetcher.FetchAsync(Repo, RenovateBodyTable.Parse(body), Ct);

        Assert.Equal(fetched, api.Calls.Count > 0);
    }

    [Theory]
    [InlineData("1.0.\uFF10")]
    [InlineData("v1.0.\u0663")]
    public async Task Fetch_SkipsAVersionWithNonAsciiDigitsWithoutFailing(string to)
    {
        var (fetcher, api) = Create();

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update(to: to), Update(dep: "other", source: "up/other", to: "v2.0.0")], Ct));

        Assert.DoesNotContain(api.Calls, c => c.Contains("upstream/dep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_NeverRequestsAPackageTagWithANonAsciiName()
    {
        var (fetcher, api) = Create();

        Assert.Empty(await fetcher.FetchAsync(Repo, [Update(dep: "d\u00e9p")], Ct));

        Assert.NotEmpty(api.Calls);
        Assert.DoesNotContain(api.Calls, c => c.Contains('\u00e9', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_ReadsASourceAndVersionOnce_WhateverTheTagPrefixOrDependencyName()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:v1.14.2"] = new GitHubRelease("v1.14.2", null, "notes");
        var updates = new[]
        {
            Update(dep: "github.com/upstream/dep/pkg/machinery", to: "v1.14.2"),
            Update(to: "1.14.2"),
            Update(to: "v1.14.2"),
            Update(dep: "next", source: "up/next", to: "v3.0.0"),
        };

        var sections = await fetcher.FetchAsync(Repo, updates, Ct);

        Assert.Equal(["github.com/upstream/dep/pkg/machinery"], sections.Select(s => s.Dependency));
        Assert.Single(api.Calls, c => c == "release:upstream/dep:v1.14.2");
    }

    [Fact]
    public async Task Fetch_CountsOnlyDistinctUpdatesAgainstTheFiveDependencyLimit()
    {
        var (fetcher, api) = Create();
        var updates = new List<RenovateUpdate> { Update(dep: "a", to: "1.0.0"), Update(dep: "b", to: "v1.0.0") };
        updates.AddRange(Enumerable.Range(1, 5).Select(i => Update(dep: $"d{i}", source: $"up/d{i}")));
        foreach (var update in updates.Skip(2))
        {
            api.Releases[$"{update.SourceRepo}:v1.2.3"] = new GitHubRelease("v1.2.3", null, "notes");
        }

        var sections = await fetcher.FetchAsync(Repo, updates, Ct);

        Assert.Equal(["d1", "d2", "d3", "d4"], sections.Select(s => s.Dependency));
    }

    [Fact]
    public async Task Fetch_ReadsAtMostFiveDependencies()
    {
        var (fetcher, api) = Create();
        var updates = Enumerable.Range(1, 8).Select(i => Update(dep: $"d{i}", source: $"up/d{i}")).ToList();
        foreach (var update in updates)
        {
            api.Releases[$"{update.SourceRepo}:v1.2.3"] = new GitHubRelease("v1.2.3", null, "notes");
        }

        var sections = await fetcher.FetchAsync(Repo, updates, Ct);

        Assert.Equal(["d1", "d2", "d3", "d4", "d5"], sections.Select(s => s.Dependency));
    }

    [Fact]
    public async Task Fetch_CutsASectionAtTheLimit()
    {
        var (fetcher, api) = Create();
        api.Releases["upstream/dep:v1.2.3"] = new GitHubRelease("v1.2.3", null, new string('x', ChangelogFetcher.MaxSectionChars + 500));

        var section = Assert.Single(await fetcher.FetchAsync(Repo, [Update()], Ct));

        Assert.Equal(ChangelogFetcher.MaxSectionChars, section.Text.Length);
    }

    [Fact]
    public async Task Fetch_LetsAGitHubFailurePropagateSoTheActivityRetries()
    {
        var (fetcher, api) = Create();
        api.Failure = new GitHubApiException("boom", System.Net.HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<GitHubApiException>(() => fetcher.FetchAsync(Repo, [Update()], Ct));
    }
}
