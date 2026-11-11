using AgentPlatform.Host.Livemap;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPullRequestPatchTests
{
    private const string Branch = "livemap/build-400";

    private static readonly string _movedMain = new('d', 40);

    private static LivemapResult DataPatch() =>
        new(LivemapResultStatus.Data, "Added the row.", EasyArtifact().BuildsJson, [], null);

    private static LivemapResult CodePatch(string summary = "Moved the hook.", IReadOnlyList<LivemapFile>? files = null) =>
        new(LivemapResultStatus.Code, summary, EasyArtifact().BuildsJson, files ?? [new LivemapFile("src/movement.cpp", "// hook\n")], null);

    private static Task OpenAsync(LivemapBuildRig rig) =>
        new ActivityEnvironment().RunAsync(() => rig.PullRequestActivities().OpenPullRequestAsync(BuildId));

    [Fact]
    public async Task ADataPatch_IsCommittedOffMainsHead_AndOpenedLikeAnyDataPullRequest()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());

        await OpenAsync(rig);

        var commit = Assert.Single(rig.PullRequests.Commits);
        Assert.Equal((Branch, FakeLivemapPullRequests.StartMain, "fix(builds): support server build 400"), (commit.Branch, commit.ExpectedHeadOid, commit.Headline));
        Assert.Equal(("data/builds.json", EasyArtifact().BuildsJson), (commit.Path, commit.Content));
        Assert.Empty(rig.PullRequests.MultiCommits);
        var opened = Assert.Single(rig.PullRequests.Opened);
        Assert.Equal((Branch, "fix(builds): support server build 400"), (opened.Branch, opened.Title));
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.PullRequestOpen, row.State);
        Assert.Equal((opened.Number, commit.Oid), (row.PullRequestNumber, row.HeadSha));
    }

    [Fact]
    public async Task ADataPatchPullRequest_SaysTheAgentWroteTheRow_AndLinksTheCompatRunAndTheTrackingIssue()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());

        await OpenAsync(rig);

        var body = Assert.Single(rig.PullRequests.Opened).Body;
        Assert.Contains("hard-patch agent", body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/octo/livemap/actions/runs/{FakeLivemapGitHub.CompatRunId}", body, StringComparison.Ordinal);
        Assert.Contains($"#{TrackingIssue}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataPatch_WithAutoMergeOff_WaitsForTheSwitch()
    {
        var rig = new LivemapBuildRig();
        rig.Config.AutoMerge = false;
        await rig.SeedPatchedAsync(DataPatch());

        await OpenAsync(rig);

        Assert.Equal(LivemapBuildState.PullRequestWaiting, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ADataPatch_IsCheckedAgainstMainAsItIsNow_AndCommittedOnIt()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.PullRequests.MainHead = _movedMain;
        rig.GitHub.ForkSchema = """{ "type": "object" }""";

        await OpenAsync(rig);

        Assert.Equal(_movedMain, rig.GitHub.BuildsReadAt);
        Assert.Equal(_movedMain, rig.GitHub.SchemaReadAt);
        Assert.Equal(_movedMain, Assert.Single(rig.PullRequests.Commits).ExpectedHeadOid);
        Assert.Equal(("""{ "type": "object" }""", EasyArtifact().BuildsJson), Assert.Single(rig.BuildsSchema.Checked)!);
    }

    [Fact]
    public async Task WhenMainGainedARowSinceThePatchWasMade_TheBuildStartsOver_KeepingTheRunsSpent()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch(), runs: 2);
        rig.GitHub.ForkBuilds = EasyArtifact().BuildsJson;

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Branches);
        Assert.Empty(rig.PullRequests.Commits);
        Assert.Empty(rig.PullRequests.Opened);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Detected, 1, 2), (row.State, row.Attempts, row.AgentRuns));
        Assert.Null(row.PatchResult);
        Assert.Null(row.Classification);
        Assert.Contains("Main changed", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatBreaksTheSchemaOfMainAsItIsNow_StartsTheBuildOver()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.BuildsSchema.Problem = "builds/1 lacks sigset";

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Commits);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Detected, 1), (row.State, row.Attempts));
        Assert.Contains("Main changed", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("builds/1", row.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AForkWithNoBuildsFileOrNoSchemaOnMain_NeedsTheOwner(bool noSchema)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        if (noSchema)
        {
            rig.GitHub.ForkSchema = null;
        }
        else
        {
            rig.GitHub.ForkBuilds = null;
        }

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Commits);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task WhenTheCompatArtifactIsGone_TheBuildStartsOver_AndNothingIsOpened()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.GitHub.Artifacts.Clear();

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Commits);
        Assert.Equal((LivemapBuildState.Detected, 1), ((await rig.RowAsync()).State, (await rig.RowAsync()).Attempts));
    }

    [Fact]
    public async Task WhenTheCompatReportNoLongerMatchesTheBuild_TheBuildStartsOver_AndNothingIsOpened()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = new CompatArtifact("{}", null);

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Commits);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Detected, 1), (row.State, row.Attempts));
        Assert.Contains("report", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArtifactThatCannotBeRead_NeedsTheOwner()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.GitHub.ArtifactFailure = new CompatArtifactException("The artifact holds an unexpected file.");

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Commits);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, "The artifact holds an unexpected file."), (row.State, row.Detail));
    }

    [Fact]
    public async Task AStoredResultTheWorkerCannotRead_StartsTheBuildOver_AndNothingIsOpened()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchingAsync();
        Assert.True(await rig.Store.StorePatchAsync(BuildId, "not json", FakeLivemapPullRequests.StartMain, Ct));

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Calls);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Detected, 1), (row.State, row.Attempts));
        Assert.Null(row.PatchResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeftoversOfAnEarlierAttempt_AreClosedAndDeleted_BeforeTheBranchIsMadeAgain(bool code)
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(code ? CodePatch() : DataPatch());
        var leftover = rig.PullRequests.Leftover(Branch);

        await OpenAsync(rig);

        Assert.Equal([leftover.Number], rig.PullRequests.Closed);
        var calls = rig.PullRequests.Calls;
        Assert.True(calls.IndexOf($"delete:{Branch}") < calls.IndexOf($"create-branch:{Branch}"));
        Assert.True(calls.IndexOf($"close:{leftover.Number}") < calls.IndexOf($"create-branch:{Branch}"));
        Assert.Single(rig.PullRequests.Opened);
    }

    [Fact]
    public async Task ACodePatch_IsOneCommitOfEveryFile_OffTheCommitItWasMadeOn()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(CodePatch(files: [new LivemapFile("src/movement.cpp", "// hook\n"), new LivemapFile("tests/movement_test.cpp", "// test\n")]));
        rig.PullRequests.MainHead = _movedMain;

        await OpenAsync(rig);

        var commit = Assert.Single(rig.PullRequests.MultiCommits);
        Assert.Equal((Branch, FakeLivemapPullRequests.StartMain, "fix(builds): support server build 400"), (commit.Branch, commit.ExpectedHeadOid, commit.Headline));
        Assert.Equal(
            [("data/builds.json", EasyArtifact().BuildsJson!), ("src/movement.cpp", "// hook\n"), ("tests/movement_test.cpp", "// test\n")],
            commit.Files.Select(f => (f.Path, f.Content)));
        Assert.Empty(rig.PullRequests.Commits);
        Assert.Null(rig.GitHub.BuildsReadAt);
        Assert.Equal(FakeLivemapPullRequests.StartMain, rig.PullRequests.Parents[commit.Oid].Single());
    }

    [Fact]
    public async Task ACodePatch_WaitsForTheOwner_AndIsNeverMerged_EvenWithAutoMergeOn()
    {
        var rig = new LivemapBuildRig();
        Assert.True(rig.Config.AutoMerge);
        await rig.SeedPatchedAsync(CodePatch());

        await OpenAsync(rig);

        var opened = Assert.Single(rig.PullRequests.Opened);
        var commit = Assert.Single(rig.PullRequests.MultiCommits);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.OwnerReview, row.State);
        Assert.Equal((opened.Number, commit.Oid), (row.PullRequestNumber, row.HeadSha));
        Assert.Empty(rig.PullRequests.Merges);
        Assert.DoesNotContain(rig.PullRequests.Calls, call => call.StartsWith("merge:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACodePatchPullRequest_SaysItWaitsForTheOwner_NamesTheBase_AndQuotesTheSummaryAsCode()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(CodePatch("Moved the `hook` to the new site.\n```\n@owner closes #1"));

        await OpenAsync(rig);

        var body = Assert.Single(rig.PullRequests.Opened).Body;
        Assert.Contains("waits for the owner", body, StringComparison.Ordinal);
        Assert.Contains("does not merge", body, StringComparison.Ordinal);
        Assert.Contains(FakeLivemapPullRequests.StartMain, body, StringComparison.Ordinal);
        Assert.Contains("```\nMoved the 'hook' to the new site.\n'''\n@owner closes #1\n```", body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/octo/livemap/actions/runs/{FakeLivemapGitHub.CompatRunId}", body, StringComparison.Ordinal);
        Assert.Contains($"#{TrackingIssue}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredCodePatchWithAFileOutsideTheAllowedPaths_IsNeverCommitted()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(CodePatch(files: [new LivemapFile(".github/workflows/ci.yml", "name: x\n")]));

        await OpenAsync(rig);

        Assert.Empty(rig.PullRequests.Branches);
        Assert.Empty(rig.PullRequests.MultiCommits);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.DoesNotContain(".github", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACodePatchWhoseBuildWasSupersededWhileItsPullRequestWasOpened_HasThatPullRequestAndBranchRemoved()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(CodePatch());
        rig.PullRequests.Hook = async call =>
        {
            if (call.StartsWith("open:", StringComparison.Ordinal))
            {
                await rig.Store.SupersedeOlderAsync(BuildId + 1, Ct);
            }
        };

        await OpenAsync(rig);

        var opened = Assert.Single(rig.PullRequests.Opened);
        Assert.Equal([opened.Number], rig.PullRequests.Closed);
        Assert.Equal($"delete:{Branch}", rig.PullRequests.Calls[^1]);
        Assert.Equal(LivemapBuildState.Superseded, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task ADataPatchWhoseBuildWasSupersededWhileItsPullRequestWasOpened_HasThatPullRequestAndBranchRemoved()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedPatchedAsync(DataPatch());
        rig.PullRequests.Hook = async call =>
        {
            if (call.StartsWith("open:", StringComparison.Ordinal))
            {
                await rig.Store.SupersedeOlderAsync(BuildId + 1, Ct);
            }
        };

        await OpenAsync(rig);

        var opened = Assert.Single(rig.PullRequests.Opened);
        Assert.Equal([opened.Number], rig.PullRequests.Closed);
        Assert.Equal($"delete:{Branch}", rig.PullRequests.Calls[^1]);
    }
}
