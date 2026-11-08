using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class ShippedBuildsReaderTests
{
    private const string PinKey = "octo/cluster:apps/livemap-pin.yaml@main";
    private const string BuildsKey = "octo/livemap:data/builds.json@v2.3.4";
    private const string Pin = "LIVEMAP_VERSION: \"v2.3.4\"\nLIVEMAP_SHA256: \"abc\"\n";
    private const string Builds = """{"builds":[{"buildids":["100","101"]},{"buildids":["200"]},{"buildids":[]}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ShippedBuildsReader Reader, FakeGitHubApi Api) Create(string? pin = Pin, string? builds = Builds)
    {
        var api = new FakeGitHubApi();
        if (pin is not null)
        {
            api.FileContents[PinKey] = pin;
        }

        if (builds is not null)
        {
            api.FileContents[BuildsKey] = builds;
        }

        var options = Options.Create(new LivemapOptions
        {
            Enabled = true,
            Repo = "octo/livemap",
            PinRepo = "octo/cluster",
            PinPath = "apps/livemap-pin.yaml",
        });
        return (new ShippedBuildsReader(api, options), api);
    }

    [Fact]
    public async Task EveryBuildIdOfEveryRow_IsShipped()
    {
        var (reader, _) = Create();

        var shipped = await reader.ReadAsync(Ct);

        Assert.Equal([100L, 101L, 200L], shipped.Order());
    }

    [Fact]
    public async Task TheDataIsReadAtTheTagTheClusterPins()
    {
        var (reader, api) = Create();

        await reader.ReadAsync(Ct);

        Assert.Equal(["default-branch", "file:octo/cluster:apps/livemap-pin.yaml@main", "file:octo/livemap:data/builds.json@v2.3.4"], api.Calls);
    }

    [Fact]
    public async Task ThePinIsReadOnTheDefaultBranchOfItsRepo()
    {
        var (reader, api) = Create();
        api.DefaultBranch = "trunk";
        api.FileContents["octo/cluster:apps/livemap-pin.yaml@trunk"] = "LIVEMAP_VERSION: \"v2.3.4\"\n";

        await reader.ReadAsync(Ct);

        Assert.Contains("file:octo/cluster:apps/livemap-pin.yaml@trunk", api.Calls);
    }

    [Theory]
    [InlineData(null, Builds)]
    [InlineData("LIVEMAP_SHA256: \"abc\"\n", Builds)]
    [InlineData(Pin, null)]
    [InlineData(Pin, "")]
    [InlineData(Pin, "not json")]
    [InlineData(Pin, "[]")]
    [InlineData(Pin, "{}")]
    [InlineData(Pin, """{"builds":{}}""")]
    [InlineData(Pin, """{"builds":[{}]}""")]
    [InlineData(Pin, """{"builds":[{"buildids":"100"}]}""")]
    [InlineData(Pin, """{"builds":[{"buildids":[100]}]}""")]
    [InlineData(Pin, """{"builds":[{"buildids":["abc"]}]}""")]
    [InlineData(Pin, """{"builds":[{"buildids":["0"]}]}""")]
    public async Task AnUnreadableSource_ThrowsInsteadOfCountingNothingAsShipped(string? pin, string? builds)
    {
        var (reader, _) = Create(pin, builds);

        await Assert.ThrowsAsync<ShippedBuildsException>(() => reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task AGitHubFailure_IsNotHiddenAsAnEmptyList()
    {
        var (reader, api) = Create();
        api.Failure = new GitHubApiException("down");

        await Assert.ThrowsAsync<GitHubApiException>(() => reader.ReadAsync(Ct));
    }
}
