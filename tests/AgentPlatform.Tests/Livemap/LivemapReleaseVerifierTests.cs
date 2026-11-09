using AgentPlatform.Host.Livemap;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.Livemap;

public class LivemapReleaseVerifierTests
{
    private const string Tag = "v2.0.0";

    private const long BuildId = 777;

    private const string Builds = """{"builds":[{"buildids":["100","777"]}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LivemapReleaseVerifier Verifier, FakeLivemapReleases Releases, FakeLivemapGitHub Github) Create()
    {
        var releases = new FakeLivemapReleases();
        releases.Seed(Tag);
        var github = new FakeLivemapGitHub { ForkBuilds = Builds };
        var options = Options.Create(new LivemapOptions { Enabled = true, Repo = FakeLivemapReleases.Fork });
        return (new LivemapReleaseVerifier(releases, github, options), releases, github);
    }

    [Fact]
    public async Task APublishedImmutableRelease_WithMatchingChecksumAndProvenance_IsVerified()
    {
        var (verifier, _, _) = Create();

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Verified, check.Outcome);
        Assert.Equal(FakeLivemapReleases.ZipSha, check.Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("  EnshroudedLiveMap-2.0.0.zip\n")]
    [InlineData(" *EnshroudedLiveMap-2.0.0.zip\r\n")]
    public async Task TheChecksumFile_NeedsOnlyItsFirstField_ToMatch(string rest)
    {
        var (verifier, releases, _) = Create();
        releases.AssetTexts[FakeLivemapReleases.ChecksumAssetId] = FakeLivemapReleases.ZipSha + rest;

        Assert.Equal(LivemapReleaseCheckOutcome.Verified, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Fact]
    public async Task TheChecksumFile_IsReadWithASizeCap()
    {
        var (verifier, releases, _) = Create();

        await verifier.VerifyAsync(Tag, BuildId, Ct);

        var read = Assert.Single(releases.AssetReads);
        Assert.Equal(FakeLivemapReleases.ChecksumAssetId, read.AssetId);
        Assert.InRange(read.MaxChars, 1, 4096);
    }

    [Fact]
    public async Task BuildsJson_IsReadAtTheCommitTheTagPointsAt()
    {
        var (verifier, _, github) = Create();

        await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(FakeLivemapReleases.TagCommit, github.BuildsReadAt);
    }

    [Fact]
    public async Task ATagWithoutARelease_IsPending()
    {
        var (verifier, releases, _) = Create();
        releases.Releases.Clear();

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Pending, check.Outcome);
        Assert.Null(check.Sha256);
    }

    [Fact]
    public async Task ADraftRelease_IsPending()
    {
        var (verifier, releases, _) = Create();
        releases.Releases[Tag] = FakeLivemapReleases.ReleaseOf(Tag, draft: true);

        Assert.Equal(LivemapReleaseCheckOutcome.Pending, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Fact]
    public async Task AReleaseThatIsNotImmutable_Fails()
    {
        var (verifier, releases, _) = Create();
        releases.Releases[Tag] = FakeLivemapReleases.ReleaseOf(Tag, immutable: false);

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, check.Outcome);
        Assert.Contains("immutable", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EnshroudedLiveMap-2.0.0.zip")]
    [InlineData("EnshroudedLiveMap-2.0.0.zip.sha256")]
    public async Task AReleaseMissingAnExpectedAsset_Fails(string missing)
    {
        var (verifier, releases, _) = Create();
        var whole = releases.Releases[Tag];
        releases.Releases[Tag] = whole with { Assets = [.. whole.Assets.Where(asset => asset.Name != missing)] };

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, check.Outcome);
        Assert.Contains(missing, check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetsOfAnotherVersion_DoNotCount()
    {
        var (verifier, releases, _) = Create();
        var whole = releases.Releases[Tag];
        releases.Releases[Tag] = whole with { Assets = [.. whole.Assets.Select(asset => asset with { Name = asset.Name.Replace("2.0.0", "1.0.0", StringComparison.Ordinal) })] };

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256:aaaa")]
    public async Task AZipWithoutAUsableDigest_Fails(string? digest)
    {
        var (verifier, releases, _) = Create();
        var whole = releases.Releases[Tag];
        releases.Releases[Tag] = whole with { Assets = [.. whole.Assets.Select(asset => asset.Name.EndsWith(".zip", StringComparison.Ordinal) ? asset with { Digest = digest } : asset)] };

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a checksum\n")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  EnshroudedLiveMap-2.0.0.zip\n")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  EnshroudedLiveMap-2.0.0.zip\n")]
    [InlineData("aaaa  EnshroudedLiveMap-2.0.0.zip\n")]
    public async Task AChecksumFile_ThatDoesNotMatchTheZipDigest_Fails(string? text)
    {
        var (verifier, releases, _) = Create();
        if (text is null)
        {
            releases.AssetTexts.Remove(FakeLivemapReleases.ChecksumAssetId);
        }
        else
        {
            releases.AssetTexts[FakeLivemapReleases.ChecksumAssetId] = text;
        }

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, check.Outcome);
        Assert.Null(check.Sha256);
    }

    [Fact]
    public async Task NoProvenance_IsPending()
    {
        var (verifier, releases, _) = Create();
        releases.Provenance.Clear();

        Assert.Equal(LivemapReleaseCheckOutcome.Pending, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Fact]
    public async Task Provenance_IsLookedUpByTheZipDigest()
    {
        var (verifier, releases, _) = Create();
        releases.Provenance.Clear();
        releases.Provenance[new string('9', 64)] = [FakeLivemapReleases.ProvenanceOf(Tag)];

        Assert.Equal(LivemapReleaseCheckOutcome.Pending, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    public static TheoryData<string, Func<LivemapProvenance, LivemapProvenance>> Mismatches => new()
    {
        { "subject digest", p => p with { Subjects = [new LivemapProvenanceSubject(FakeLivemapReleases.AssetName(Tag), new string('9', 64))] } },
        { "subject name", p => p with { Subjects = [new LivemapProvenanceSubject("EnshroudedLiveMap-9.9.9.zip", FakeLivemapReleases.ZipSha)] } },
        { "no subject", p => p with { Subjects = [] } },
        { "workflow path", p => p with { WorkflowPath = ".github/workflows/ci.yaml" } },
        { "tag ref", p => p with { WorkflowRef = "refs/tags/v1.0.0" } },
        { "branch ref", p => p with { WorkflowRef = "refs/heads/main" } },
        { "repository", p => p with { Repository = "https://github.com/someone/else" } },
        { "commit", p => p with { CommitSha = new string('e', 40) } },
        { "missing commit", p => p with { CommitSha = null } },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task Provenance_ThatDoesNotMatchOnAnyPoint_Fails(string point, Func<LivemapProvenance, LivemapProvenance> change)
    {
        var (verifier, releases, _) = Create();
        releases.Provenance[FakeLivemapReleases.ZipSha] = [change(FakeLivemapReleases.ProvenanceOf(Tag))];

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.True(check.Outcome == LivemapReleaseCheckOutcome.Failed, point);
        Assert.Null(check.Sha256);
    }

    [Fact]
    public async Task OneMatchingStatement_AmongOthers_IsEnough()
    {
        var (verifier, releases, _) = Create();
        var other = FakeLivemapReleases.ProvenanceOf(Tag) with { WorkflowPath = ".github/workflows/ci.yaml" };
        releases.Provenance[FakeLivemapReleases.ZipSha] = [other, FakeLivemapReleases.ProvenanceOf(Tag)];

        Assert.Equal(LivemapReleaseCheckOutcome.Verified, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Fact]
    public async Task ATagWithoutACommit_Fails()
    {
        var (verifier, releases, _) = Create();
        releases.TagCommits.Clear();

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, (await verifier.VerifyAsync(Tag, BuildId, Ct)).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"builds":[{"buildids":["100"]}]}""")]
    [InlineData("""{"builds":[{"buildids":["7770"]}]}""")]
    public async Task AReleaseWhoseBuildList_DoesNotHoldTheBuild_Fails(string? builds)
    {
        var (verifier, _, github) = Create();
        github.ForkBuilds = builds;

        var check = await verifier.VerifyAsync(Tag, BuildId, Ct);

        Assert.Equal(LivemapReleaseCheckOutcome.Failed, check.Outcome);
        Assert.Null(check.Sha256);
    }

    [Fact]
    public async Task ABadTag_IsRefusedBeforeAnyRead()
    {
        var (verifier, releases, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => verifier.VerifyAsync("main", BuildId, Ct));

        Assert.Empty(releases.Calls);
    }

    [Fact]
    public async Task TheLatestRelease_IsFoundWhenItsBuildListHoldsTheBuild()
    {
        var (verifier, releases, github) = Create();

        var found = await verifier.FindReleaseWithBuildAsync(BuildId, Ct);

        Assert.Equal(Tag, found!.Tag);
        Assert.Equal(FakeLivemapReleases.TagCommit, github.BuildsReadAt);
        Assert.Contains("latest", releases.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"builds":[{"buildids":["100"]}]}""")]
    public async Task TheLatestRelease_IsNotFound_WhenItsBuildListLacksTheBuild(string? builds)
    {
        var (verifier, _, github) = Create();
        github.ForkBuilds = builds;

        Assert.Null(await verifier.FindReleaseWithBuildAsync(BuildId, Ct));
    }

    [Fact]
    public async Task ALatestReleaseWithoutAPlainReleaseTag_IsNotFound_AndNoCommitIsRead()
    {
        var (verifier, releases, _) = Create();
        releases.Latest = FakeLivemapReleases.ReleaseOf("v2.0.0-rc1");

        Assert.Null(await verifier.FindReleaseWithBuildAsync(BuildId, Ct));

        Assert.DoesNotContain(releases.Calls, call => call.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAReleaseOrACommit_NothingIsFound()
    {
        var (verifier, releases, _) = Create();
        releases.TagCommits.Clear();
        Assert.Null(await verifier.FindReleaseWithBuildAsync(BuildId, Ct));

        releases.Latest = null;
        Assert.Null(await verifier.FindReleaseWithBuildAsync(BuildId, Ct));
    }
}
