using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.RunData;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeRungActivitiesTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = FakeGitHubApi.Sha;
    private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";

    private sealed record Rig(ClaudeRungActivities Activities, FakeGitHubApi Github, InMemoryRunData Data);

    private static Rig Create(string model = "opus", TriageMode mode = TriageMode.Live)
    {
        var github = new FakeGitHubApi();
        var data = InMemoryRunData.Create();
        return new Rig(new ClaudeRungActivities(github, data.Pending, new AgentPodsOptions { Model = model }, Options.Create(new RenovateTriageOptions { Mode = mode })), github, data);
    }

    private static Task<T> Run<T>(Func<Task<T>> action) => new ActivityEnvironment().RunAsync(action);

    [Fact]
    public async Task Queue_WritesThePendingRow_TheLabelAndTheQueuedNote()
    {
        var rig = Create();

        var result = await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.False(result.HeadMoved);
        Assert.Equal("opus", result.Model);
        var row = await rig.Data.Pending.FindAsync(Repo, 7, Ct);
        Assert.Equal((Sha, "breaking-change", TriagePendingStatus.Pending), (row!.HeadSha, row.Reason, row.Status));
        Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.AddedLabels);
        var note = Assert.Single(rig.Github.Comments);
        Assert.Contains(TriageRules.StickyMarker, note.Body, StringComparison.Ordinal);
        Assert.Contains("**Queued for agent review:** breaking-change", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_FlagsAPullRequestThatCarriesTheSecurityLabel()
    {
        var rig = Create();
        rig.Github.PullRequest = rig.Github.PullRequest with { Labels = ["dependencies", TriageRules.SecurityLabel] };

        var result = await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.True(result.Security);
    }

    [Fact]
    public async Task Queue_DoesNotFlagAPullRequestWithoutTheSecurityLabel()
    {
        var rig = Create();

        var result = await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.False(result.Security);
    }

    [Fact]
    public async Task Queue_ClearsAnOlderHeadsVerdictLabel_ButNotTheBlockedLabel()
    {
        var rig = Create();
        rig.Github.PullRequest = rig.Github.PullRequest with { Labels = [TriageRules.SafeLabel, TriageRules.BlockedLabel] };

        await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.Equal([TriageRules.SafeLabel], rig.Github.RemovedLabels);
        Assert.Equal([TriageRules.NeedsTriageLabel], rig.Github.AddedLabels);
        Assert.Contains(TriageRules.BlockedLabel, rig.Github.PullRequest.Labels);
    }

    [Fact]
    public async Task Queue_WhenTheModeIsNoLongerLive_WritesNothing()
    {
        var rig = Create(mode: TriageMode.Shadow);

        var result = await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.True(result.NotLive);
        Assert.False(result.HeadMoved);
        Assert.Null(await rig.Data.Pending.FindAsync(Repo, 7, Ct));
        Assert.Empty(rig.Github.AddedLabels);
        Assert.Empty(rig.Github.Comments);
    }

    [Fact]
    public async Task Queue_WritesTheRowBeforeTheLabel_SoALabelNeverOutlivesItsRow()
    {
        var rig = Create();
        rig.Github.WriteFailure = new InvalidOperationException("GitHub is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change"))));

        Assert.NotNull(await rig.Data.Pending.FindAsync(Repo, 7, Ct));
        Assert.Empty(rig.Github.AddedLabels);
    }

    [Fact]
    public async Task Queue_OnARetry_UpdatesTheNoteInPlace()
    {
        var rig = Create();

        await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));
        await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, Sha, "breaking-change")));

        Assert.Single(rig.Github.Comments);
        Assert.Equal(1, await rig.Data.Pending.CountOpenAsync(Ct));
    }

    [Fact]
    public async Task Queue_ForAMovedHead_WritesNothing()
    {
        var rig = Create();

        var result = await Run(() => rig.Activities.QueueAsync(new QueueInput(Repo, 7, OtherSha, "breaking-change")));

        Assert.True(result.HeadMoved);
        Assert.Null(await rig.Data.Pending.FindAsync(Repo, 7, Ct));
        Assert.Empty(rig.Github.Writes);
    }

    [Fact]
    public async Task Queue_ForABadRepo_FailsWithoutRetry()
    {
        var rig = Create();

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() => Run(() => rig.Activities.QueueAsync(new QueueInput("not-a-repo", 7, Sha, "x"))));

        Assert.True(ex.NonRetryable);
    }

    [Fact]
    public async Task ReadHead_ReturnsTheHeadOfThePullRequest()
    {
        var rig = Create();

        Assert.Equal(Sha, await Run(() => rig.Activities.ReadHeadAsync(new ReadHeadInput(Repo, 7))));
        rig.Github.PullRequest = FakeGitHubApi.Renovate(sha: OtherSha);
        Assert.Equal(OtherSha, await Run(() => rig.Activities.ReadHeadAsync(new ReadHeadInput(Repo, 7))));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
