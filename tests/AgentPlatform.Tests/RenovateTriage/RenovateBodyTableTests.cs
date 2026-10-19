using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class RenovateBodyTableTests
{
    private const string Footer = "\n\n---\n\n### Release Notes\n\n<details>\n<summary>step-security/harden-runner (step-security/harden-runner)</summary>\n</details>\n\n---\n\n### Configuration\n";

    [Fact]
    public void Parse_ReadsAPackageLinkAndAFromToChange()
    {
        const string body = """
            This PR contains the following updates:

            | Package | Type | Update | Change |
            |---|---|---|---|
            | [step-security/harden-runner](https://redirect.github.com/step-security/harden-runner) | action | minor | `v2.21.1` → `v2.22.1` |

            ---
            """;

        var update = Assert.Single(RenovateBodyTable.Parse(body));

        Assert.Equal(new RenovateUpdate("step-security/harden-runner", "step-security/harden-runner", "v2.21.1", "v2.22.1"), update);
    }

    [Fact]
    public void Parse_ReadsAChangeThatIsItselfALink()
    {
        const string body = """
            | Package | Change | [Age](https://docs.renovatebot.com/merge-confidence/) |
            |---|---|---|
            | [golang.org/x/net](https://pkg.go.dev/golang.org/x/net) | [`v0.58.0` → `v0.60.0`](https://cs.opensource.google/go/x/net/+/refs/tags/v0.58.0...refs/tags/v0.60.0) | ![age](https://developer.mend.io/x) |
            """;

        var update = Assert.Single(RenovateBodyTable.Parse(body));

        Assert.Equal("golang.org/x/net", update.Dependency);
        Assert.Null(update.SourceRepo);
        Assert.Equal("v0.58.0", update.From);
        Assert.Equal("v0.60.0", update.To);
    }

    [Fact]
    public void Parse_ReadsEveryRowOfAGroupedPr()
    {
        const string body = """
            | Package | Update | Change |
            |---|---|---|
            | [a/one](https://github.com/a/one) | patch | `1.0.0` → `1.0.1` |
            | [two](https://redirect.github.com/b/two/) | minor | `2.0.0` → `2.1.0` |
            | [three](https://example.test/three) | major | `3.0.0` → `4.0.0` |
            """;

        var updates = RenovateBodyTable.Parse(body);

        Assert.Equal(["a/one", "two", "three"], updates.Select(u => u.Dependency));
        Assert.Equal(["a/one", "b/two", null], updates.Select(u => u.SourceRepo));
    }

    [Theory]
    [InlineData("https://github.com.evil.test/a/b")]
    [InlineData("https://evil.test/github.com/a/b")]
    [InlineData("http://github.com/a/b")]
    [InlineData("https://github.com/a")]
    [InlineData("https://github.com.evil.test/a/b/tree/HEAD/x")]
    [InlineData("https://evil.test/github.com/a/b/tree/HEAD/x")]
    [InlineData("http://github.com/a/b/tree/HEAD/x")]
    [InlineData("https://github.com/a#b/c")]
    [InlineData("https://github.com/a/b?x=1")]
    [InlineData("https://github.com/a/b%2Fc")]
    public void Parse_OnlyTrustsAnHttpsGitHubRepoLinkAsTheSource(string url)
    {
        var body = $"| Package | Change |\n|---|---|\n| [dep]({url}) | `1.0.0` → `1.0.1` |\n";

        Assert.Null(Assert.Single(RenovateBodyTable.Parse(body)).SourceRepo);
    }

    [Theory]
    [InlineData("https://github.com/a/b/tree/HEAD/packages/b", "a/b")]
    [InlineData("https://redirect.github.com/a/b/tree/main/x/y/", "a/b")]
    [InlineData("https://github.com/a/b/c/d", "a/b")]
    [InlineData("https://github.com/a/b/", "a/b")]
    public void Parse_ReadsTheSourceRepoOutOfARepoLinkWithAPath(string url, string source)
    {
        var body = $"| Package | Change |\n|---|---|\n| [dep](https://example.test) ([source]({url})) | `1.0.0` → `1.0.1` |\n";

        Assert.Equal(source, Assert.Single(RenovateBodyTable.Parse(body)).SourceRepo);
    }

    [Theory]
    [InlineData("~> 0.76", "~> 0.78")]
    [InlineData(">= 1.2, < 2", ">= 1.3, < 2")]
    [InlineData("1.2.3-alpine", "1.2.4-alpine")]
    [InlineData("^1.0.0", "^1.1.0")]
    public void Parse_ReadsAChangeWhoseVersionsHoldSpacesOrOperators(string from, string to)
    {
        var body = $"| Package | Change |\n|---|---|\n| [dep](https://github.com/a/b) | `{from}` → `{to}` |\n";

        var update = Assert.Single(RenovateBodyTable.Parse(body));

        Assert.Equal((from, to), (update.From, update.To));
    }

    [Fact]
    public void Parse_ReadsTheRowsOfARealTerraformAndAMonorepoBody()
    {
        var terraform = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", "release-notes-spruyt-labs-2188.txt"));
        var monorepo = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RenovateTriage", "Fixtures", "release-notes-spruyt-labs-2536.txt"));

        Assert.Equal([new RenovateUpdate("tfe", "hashicorp/terraform-provider-tfe", "~> 0.76", "~> 0.78")], RenovateBodyTable.Parse(terraform));
        Assert.Equal(
            [("@vitest/coverage-v8", "vitest-dev/vitest", "4.1.11"), ("vitest", "vitest-dev/vitest", "4.1.11")],
            RenovateBodyTable.Parse(monorepo).Select(u => (u.Dependency, u.SourceRepo, u.To)));
    }

    [Theory]
    [InlineData("[cloudflare](https://registry.terraform.io/providers/cloudflare/cloudflare) ([source](https://redirect.github.com/cloudflare/terraform-provider-cloudflare))", "cloudflare", "cloudflare/terraform-provider-cloudflare")]
    [InlineData("[n8nio/n8n](https://n8n.io) ([source](https://redirect.github.com/n8n-io/n8n))", "n8nio/n8n", "n8n-io/n8n")]
    [InlineData("[dep](https://github.com/home/page) ([source](https://github.com/up/stream))", "dep", "up/stream")]
    [InlineData("[dep](https://example.test) ([changelog](https://github.com/up/stream))", "dep", "up/stream")]
    [InlineData("[dep](https://example.test) ([source](https://example.test/up/stream))", "dep", null)]
    public void Parse_TakesTheSourceLinkOverTheHomepage(string packageCell, string dependency, string? source)
    {
        var body = $"| Package | Change |\n|---|---|\n| {packageCell} | `1.0.0` → `1.1.0` |\n";

        var update = Assert.Single(RenovateBodyTable.Parse(body));

        Assert.Equal((dependency, source), (update.Dependency, update.SourceRepo));
    }

    [Fact]
    public void Parse_AcceptsAnAsciiArrow()
    {
        var body = "| Package | Change |\n|---|---|\n| [dep](https://github.com/a/b) | `1.0.0` -> `1.0.1` |\n";

        var update = Assert.Single(RenovateBodyTable.Parse(body));

        Assert.Equal(("1.0.0", "1.0.1"), (update.From, update.To));
    }

    [Fact]
    public void Parse_SkipsRowsItCannotRead()
    {
        const string body = """
            | Package | Change |
            |---|---|
            | [dep](https://github.com/a/b) | `1.0.0` |
            | [dep2](https://github.com/a/c) | `1.0.0` → `1.0.1` |
            | | `1.0.0` → `1.0.1` |
            """;

        Assert.Equal(["dep2"], RenovateBodyTable.Parse(body).Select(u => u.Dependency));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no table here")]
    [InlineData("| Name | Value |\n|---|---|\n| [a](https://github.com/a/b) | `1` → `2` |")]
    public void Parse_WithoutAPackageTable_ReturnsNothing(string? body) =>
        Assert.Empty(RenovateBodyTable.Parse(body));

    [Fact]
    public void Parse_ReadsOnlyTheFirstTable()
    {
        var body = "| Package | Change |\n|---|---|\n| [a](https://github.com/a/a) | `1.0.0` → `1.0.1` |\n" + Footer
            + "\n| Package | Change |\n|---|---|\n| [evil](https://github.com/e/e) | `1.0.0` → `9.9.9` |\n";

        Assert.Equal(["a"], RenovateBodyTable.Parse(body).Select(u => u.Dependency));
    }

    [Fact]
    public void Parse_KeepsAtMostFiftyRows()
    {
        var rows = string.Concat(Enumerable.Range(1, 80).Select(i => $"| [d{i}](https://github.com/a/d{i}) | `1.0.0` → `1.0.1` |\n"));

        Assert.Equal(RenovateBodyTable.MaxRows, RenovateBodyTable.Parse("| Package | Change |\n|---|---|\n" + rows).Count);
    }
}
