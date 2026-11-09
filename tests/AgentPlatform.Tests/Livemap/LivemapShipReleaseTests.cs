using AgentPlatform.Host.Livemap;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;
using static AgentPlatform.Tests.Livemap.LivemapShipRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapShipReleaseTests
{
    private static readonly TimeSpan _pastReleaseLimit = TimeSpan.FromHours(7);

    private static readonly TimeSpan _pastPublishLimit = TimeSpan.FromHours(3);

    private static readonly Dictionary<string, Action<LivemapShipRig>> _notReady = new()
    {
        { "check still running", rig => rig.Fork.Checks[ReleaseHead] = [.. ReleaseChecks.Select((name, i) => i == 0 ? CheckRun(name, "in_progress", null, suite: ReleaseSuiteId) : CheckRun(name, suite: ReleaseSuiteId))] },
        { "check failed", rig => rig.Fork.Checks[ReleaseHead] = [.. ReleaseChecks.Select((name, i) => i == 1 ? CheckRun(name, conclusion: "failure", suite: ReleaseSuiteId) : CheckRun(name, suite: ReleaseSuiteId))] },
        { "release-only check missing", rig => rig.Fork.Checks[ReleaseHead] = [.. ReleaseChecks.Where(name => name != "summary / Check Results").Select(name => CheckRun(name, suite: ReleaseSuiteId))] },
        { "checks from another workflow", rig => rig.GitHub.SuiteRunsBySuite[ReleaseSuiteId] = [CiRun(branch: "other")] },
        { "not the release bot", rig => rig.Fork.Pulls[ReleaseNumber] = rig.Fork.Pulls[ReleaseNumber] with { AuthorLogin = "someone[bot]" } },
        { "a file beyond the release files", rig => rig.Fork.CommitFiles[ReleaseHead].Add(new LivemapPullRequestFile("data/builds.json", "modified", null, "@@")) },
        { "version.txt is not the release version", rig => rig.Fork.Contents[(ReleaseHead, "version.txt")] = "9.9.9\n" },
        { "draft", rig => rig.Fork.Pulls[ReleaseNumber] = rig.Fork.Pulls[ReleaseNumber] with { Draft = true } },
    };

    public static TheoryData<string> NotReady()
    {
        var data = new TheoryData<string>();
        foreach (var point in _notReady.Keys)
        {
            data.Add(point);
        }

        return data;
    }

    [Fact]
    public async Task AGreenReleasePullRequest_IsSquashMerged_AtItsHead_AndTheBuildIsReleasedUnderTheTagFromItsTitle()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal((ReleaseNumber, ReleaseHead, "chore(main): release 2.0.0 (#61)"), Assert.Single(rig.Fork.Merges));
        Assert.Empty(rig.Pin.Merges);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Released, row.State);
        Assert.Equal(Tag, row.ReleaseTag);
        Assert.Equal(Now, row.ReleasedAt);
    }

    [Fact]
    public async Task AReleasePullRequestMadeOnAnOlderCommitOfMain_IsMergedAtItsHead()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.Parents[ReleaseHead] = [new string('e', 40)];

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(ReleaseHead, Assert.Single(rig.Fork.Merges).Sha);
        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task TheReleaseGate_ReadsTheReleaseChecks_NotTheDataPullRequestChecks()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Config.RequiredChecks = ["a data check nobody runs on a release"];

        await rig.ShipAsync();

        Assert.Single(rig.Fork.Merges);
    }

    [Fact]
    public async Task WithoutAReleasePullRequest_TheBuildWaits()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task SeveralReleasePullRequests_AreNotMerged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        var pull = rig.SeedReleasePullRequest();
        rig.Fork.Seed(pull with { Number = 62, HeadSha = new string('d', 40) });

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Empty(rig.Fork.Merges);
    }

    [Theory]
    [MemberData(nameof(NotReady))]
    public async Task AReleasePullRequestThatIsNotReady_IsNotMerged_AndTheBuildWaits(string point)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        _notReady[point](rig);

        var wait = await rig.ShipAsync();

        Assert.True(wait, point);
        Assert.Empty(rig.Fork.Merges);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Theory]
    [MemberData(nameof(NotReady))]
    public async Task AReleasePullRequestThatStaysNotReady_IsFlaggedForAPerson_OnceTheLimitPasses(string point)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        _notReady[point](rig);

        var wait = await rig.ShipAsync(After(_pastReleaseLimit));

        Assert.False(wait, point);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.False(string.IsNullOrWhiteSpace(row.Detail), point);
        Assert.Empty(rig.Fork.Merges);
    }

    [Fact]
    public async Task AFailedCheck_IsNamedInWhatTheOwnerReads()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.Checks[ReleaseHead] = [.. ReleaseChecks.Select((name, i) => i == 1 ? CheckRun(name, conclusion: "failure", suite: ReleaseSuiteId) : CheckRun(name, suite: ReleaseSuiteId))];

        await rig.ShipAsync(After(_pastReleaseLimit));

        Assert.Contains(ReleaseChecks[1], (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoReleasePullRequest_TheBuildIsFlagged_OnceTheLimitPasses()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);

        var wait = await rig.ShipAsync(After(_pastReleaseLimit));

        Assert.False(wait);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ANotMergeableAnswer_IsAskedAgain_ThreeTimes_FiveSecondsApart()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.NotMergeableAnswers = 2;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(3, rig.Fork.Merges.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], rig.Time.Waits);
        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeThatStaysNotMergeable_IsGivenUp_AndTheBuildWaits()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.NotMergeableAnswers = 99;

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal(4, rig.Fork.Merges.Count);
        Assert.Equal(3, rig.Time.Waits.Count);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeWhoseAnswerWasLost_IsRecognisedWhenThePullRequestIsMergedAtTheSameHead()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.MergeResult = LivemapMergeResult.Refused;
        rig.Fork.Hook = call =>
        {
            if (call == $"merge:{ReleaseNumber}")
            {
                rig.Fork.Pulls[ReleaseNumber] = rig.Fork.Pulls[ReleaseNumber] with { State = "closed", Merged = true };
            }

            return Task.CompletedTask;
        };

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APullRequestMergedAtAnotherHead_IsNotTakenForTheWorkersMerge()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.MergeResult = LivemapMergeResult.Refused;
        rig.Fork.Hook = call =>
        {
            if (call == $"merge:{ReleaseNumber}")
            {
                rig.Fork.Pulls[ReleaseNumber] = rig.Fork.Pulls[ReleaseNumber] with { State = "closed", Merged = true, HeadSha = "someone-elses-head" };
            }

            return Task.CompletedTask;
        };

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AMergeGitHubRefuses_LeavesTheBuildWaiting()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.MergeResult = LivemapMergeResult.Refused;

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Single(rig.Fork.Merges);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WhenMainMovesAfterTheChecksAreRead_NothingIsMerged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Fork.Hook = call =>
        {
            if (call == $"checks:{ReleaseHead}")
            {
                rig.Fork.MainHead = new string('e', 40);
            }

            return Task.CompletedTask;
        };

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Empty(rig.Fork.Merges);
    }

    [Fact]
    public async Task AReleaseThatListsTheBuild_IsTakenAsReleased_WithoutTouchingTheReleasePullRequest()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.PublishRelease();

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Released, row.State);
        Assert.Equal(Tag, row.ReleaseTag);
        Assert.Empty(rig.Fork.Merges);
        Assert.Empty(rig.Fork.Calls);
    }

    [Fact]
    public async Task AReleaseThatDoesNotListTheBuild_IsNotTakenForIt()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.PublishRelease();
        rig.GitHub.ForkBuilds = ForkBuilds;

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WithAutoMergeOff_NothingIsMerged_AndTheBuildWaitsWhereThePollResumesIt()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.SeedReleasePullRequest();
        rig.Config.AutoMerge = false;

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Fork.Merges);
        Assert.Empty(rig.Fork.Calls);
        Assert.Equal(LivemapBuildState.ReleaseWaiting, (await rig.RowAsync()).State);
        Assert.True(await rig.Store.ResumeWaitingAsync(BuildId, Ct));
        Assert.Equal(LivemapBuildState.Merged, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WithAutoMergeOff_ARelease_ThatIsAlreadyOut_StillCounts()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Merged);
        rig.PublishRelease();
        rig.Config.AutoMerge = false;

        await rig.ShipAsync();

        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ARelease_WithItsAssetsAndAttestation_IsVerified_AndTheBuildKeepsTheZipDigest()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.PublishRelease();

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Verified, row.State);
        Assert.Equal(FakeLivemapReleases.ZipSha, row.ReleaseSha256);
    }

    [Fact]
    public async Task ARelease_NotPublishedYet_IsWaitedFor()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "in_progress", null, 1, FakeLivemapReleases.TagCommit, "https://github.example/run");

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
        Assert.Empty(rig.Releases.Reruns);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    public async Task AFailedPublishRun_HasItsFailedJobsRunAgainOnce(string conclusion)
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "completed", conclusion, 1, FakeLivemapReleases.TagCommit, "https://github.example/run");

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Equal([TagRunId], rig.Releases.Reruns);
        Assert.Equal(LivemapBuildState.Released, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task APublishRunThatFailedASecondTime_EndsTheBuildForAPerson_WithoutAThirdRun()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "completed", "failure", 2, FakeLivemapReleases.TagCommit, "https://github.example/run");

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Empty(rig.Releases.Reruns);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("twice", row.Detail, StringComparison.Ordinal);
        Assert.Contains("https://github.example/run", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishRunThatSucceeded_IsNotRunAgain_WhileTheAttestationIsAwaited()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.PublishRelease();
        rig.Releases.Provenance.Clear();
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "completed", "success", 1, FakeLivemapReleases.TagCommit, "https://github.example/run");

        var wait = await rig.ShipAsync();

        Assert.True(wait);
        Assert.Empty(rig.Releases.Reruns);
    }

    [Fact]
    public async Task ARelease_StillUnverified_AfterThePublishLimit_IsFlaggedWithWhatIsMissing()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.PublishRelease();
        rig.Releases.Provenance.Clear();
        rig.Releases.Runs[Tag] = new LivemapTagRun(TagRunId, "completed", "success", 1, FakeLivemapReleases.TagCommit, "https://github.example/run");

        var wait = await rig.ShipAsync(After(_pastPublishLimit));

        Assert.False(wait);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("provenance", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARelease_ThatIsNotPublishedByThePublishLimit_IsFlagged()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);

        var wait = await rig.ShipAsync(After(_pastPublishLimit));

        Assert.False(wait);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task AReleaseThatIsNotImmutable_IsFlaggedAtOnce_NotWaitedFor()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.PublishRelease();
        rig.Releases.Releases[Tag] = FakeLivemapReleases.ReleaseOf(Tag, immutable: false);

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("immutable", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChecksumThatDoesNotMatchTheZip_IsFlaggedAtOnce()
    {
        var rig = new LivemapShipRig();
        await rig.AtAsync(LivemapBuildState.Released);
        rig.PublishRelease();
        rig.Releases.AssetTexts[FakeLivemapReleases.ChecksumAssetId] = new string('f', 64) + "  file.zip\n";

        var wait = await rig.ShipAsync();

        Assert.False(wait);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }
}
