using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Sweeper;

public class SweeperActivitiesTests
{
    private const string Repo = "anthony-spruyt/widgets";
    private const string Sha = FakeGitHubApi.Sha;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class Rig
    {
        public Rig(TriageMode mode = TriageMode.Live, int perHour = 2)
        {
            Now = DateTimeOffset.UtcNow;
            var options = Options.Create(new RenovateTriageOptions { Mode = mode, Claude = new ClaudeBudgetOptions { MaxPerHour = perHour } });
            var time = new FixedTimeProvider(Now);
            Sweeper = new SweeperStore(Data.Factory, time);
            Activities = new SweeperActivities(
                Github,
                App,
                Data.Pending,
                new ClaudeBudgetStore(Data.Factory, options, time),
                Data.Verdicts,
                Sweeper,
                Signaler,
                Probe,
                options,
                time,
                NullLogger<SweeperActivities>.Instance);
        }

        public DateTimeOffset Now { get; }

        public FakeGitHubApi Github { get; } = new();

        public FakeGitHubAppClient App { get; } = new();

        public InMemoryRunData Data { get; } = InMemoryRunData.Create();

        public RecordingSignaler Signaler { get; } = new();

        public FakeWorkflowProbe Probe { get; } = new();

        public SweeperStore Sweeper { get; }

        public SweeperActivities Activities { get; }

        public async Task ParkAsync(int pr, string sha = Sha)
        {
            await Data.Pending.UpsertAsync(Repo, pr, sha, "claude-pending", Ct);
            await Task.Delay(5, Ct);
        }

        public async Task<TriagePendingStatus> StatusAsync(int pr) => (await Data.Pending.FindAsync(Repo, pr, Ct))!.Status;

        public void OpenPullRequest(int number, params string[] labels) =>
            Github.PullRequestsByNumber[number] = (FakeGitHubApi.Renovate() with { Number = number, Labels = labels });

        public async Task CooldownAsync()
        {
            await using var db = await Data.Factory.CreateDbContextAsync(Ct);
            db.ClaudeCooldowns.Add(new ClaudeCooldown { Scope = ClaudeCooldown.Global, Until = Now.AddHours(1), Cause = "usage-limit" });
            await db.SaveChangesAsync(Ct);
        }

        public void OpenCheck(int pr, string conclusion = "", string status = "in_progress")
        {
            var run = new GitHubCheckRunWrite(TriageCheckActivities.CheckName, Sha, TriageCheckActivities.ExternalId(Repo, pr, Sha), status, status == "completed" ? conclusion : null, TriageCheckActivities.CheckName, "s");
            Github.StoredCheckRuns.Add((700 + Github.StoredCheckRuns.Count, run));
        }
    }

    private static Task<T> Run<T>(Func<Task<T>> action) => new ActivityEnvironment().RunAsync(action);

    [Fact]
    public async Task AParkedPullRequest_IsRedrivenWithASignal_AndMarkedRedriven()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);

        var result = await Run(rig.Activities.RedriveAsync);

        var signal = Assert.Single(rig.Signaler.Signals);
        Assert.Equal((Repo, 7, Sha), (signal.Repo, signal.PrNumber, signal.HeadSha));
        Assert.Equal(TriagePendingStatus.Redriven, await rig.StatusAsync(7));
        Assert.Equal(new RedriveResult(1, 0, 0, 1, 0, 0), result);
    }

    [Fact]
    public async Task AParkedPullRequest_IsRedrivenOnItsCurrentHead_WhenTheHeadMovedSinceParking()
    {
        var rig = new Rig();
        await rig.ParkAsync(7, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        rig.OpenPullRequest(7);

        await Run(rig.Activities.RedriveAsync);

        Assert.Equal(Sha, Assert.Single(rig.Signaler.Signals).HeadSha);
    }

    [Fact]
    public async Task WhileTheGlobalCooldownIsActive_NothingIsRedriven()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        await rig.CooldownAsync();

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(7));
        Assert.Equal(0, result.Redriven);
    }

    [Fact]
    public async Task TheRedrives_AreBoundedByTheHourlyCap_OldestFirst()
    {
        var rig = new Rig(perHour: 2);
        foreach (var pr in new[] { 11, 12, 13 })
        {
            await rig.ParkAsync(pr);
            rig.OpenPullRequest(pr);
        }

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Equal([11, 12], rig.Signaler.Signals.Select(s => s.PrNumber));
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(13));
        Assert.Equal(2, result.Redriven);
    }

    [Fact]
    public async Task ARedrivenRowThatParkedAgain_IsRedrivenAgain()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        await rig.Data.Pending.SetStatusAsync(Repo, 7, Sha, TriagePendingStatus.Redriven, Ct);

        await Run(rig.Activities.RedriveAsync);

        Assert.Single(rig.Signaler.Signals);
    }

    [Fact]
    public async Task APullRequestLabelledNeedsHumanReview_IsNeverRedriven_AndItsOpenCheckIsClosedNeutral()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7, TriageRules.NeedsReviewLabel);
        rig.OpenCheck(7);

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.Owner, await rig.StatusAsync(7));
        Assert.Equal("neutral", Assert.Single(rig.Github.StoredCheckRuns).Run.Conclusion);
        Assert.Equal("completed", rig.Github.StoredCheckRuns[0].Run.Status);
        Assert.Equal(1, result.Settled);
    }

    [Fact]
    public async Task APullRequestTheOwnerReviewedAfterItParked_IsSkipped_AndMarkedOwnerHandled()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.AllowedOwner, "OWNER", "COMMENTED", Sha, rig.Now.AddMinutes(5)));

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(7));
    }

    [Fact]
    public async Task APullRequestTheOwnerLabelledAfterItParked_IsSkipped_AndMarkedOwnerHandled()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Github.IssueEvents.Add(new GitHubIssueEvent("labeled", TriageRules.AllowedOwner, TriageRules.BlockedLabel, rig.Now.AddMinutes(5)));

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(7));
    }

    [Fact]
    public async Task AnOwnerReview_StillSettlesThePullRequest_AfterAFailedAttemptBumpedTheRow()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Github.Reviews.Add(new GitHubReview(1, TriageRules.AllowedOwner, "OWNER", "COMMENTED", Sha, rig.Now.AddMinutes(5)));
        await using (var db = await rig.Data.Factory.CreateDbContextAsync(Ct))
        {
            (await db.TriagePending.SingleAsync(Ct)).UpdatedAt = rig.Now.AddMinutes(10);
            await db.SaveChangesAsync(Ct);
        }

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(7));
    }

    [Theory]
    [InlineData(TriageRules.BlockedLabel)]
    [InlineData(TriageRules.RevertLabel)]
    public async Task ARowWhosePullRequestCarriesABlockingLabel_IsNotRedriven_AndDoesNotUseUpTheCap(string label)
    {
        var rig = new Rig(perHour: 2);
        foreach (var pr in new[] { 1, 2, 3 })
        {
            await rig.ParkAsync(pr);
            await rig.Data.Pending.SetStatusAsync(Repo, pr, Sha, TriagePendingStatus.Redriven, Ct);
        }

        rig.OpenPullRequest(1, label);
        rig.OpenPullRequest(2, label);
        rig.OpenPullRequest(3);

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Equal([3], rig.Signaler.Signals.Select(s => s.PrNumber));
        Assert.Equal(TriagePendingStatus.Redriven, await rig.StatusAsync(1));
        Assert.Equal((1, 2), (result.Redriven, result.Skipped));
    }

    [Fact]
    public async Task ARowWhosePullRequestCarriesBlocked_IsMarkedHeld_AndStaysOpenButUncounted()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7, TriageRules.BlockedLabel);

        var result = await Run(rig.Activities.RedriveAsync);

        var row = (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!;
        Assert.NotNull(row.HeldAt);
        Assert.Equal(TriagePendingStatus.Pending, row.Status);
        Assert.Equal(0, await rig.Data.Pending.CountOpenAsync(Ct));
        Assert.Single(await rig.Data.Pending.ListOpenAsync(Ct));
        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task WhenBlockedComesOff_TheHoldIsCleared_AndTheRowIsRedrivenInTheSameRun()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7, TriageRules.BlockedLabel);
        await Run(rig.Activities.RedriveAsync);
        Assert.NotNull((await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.HeldAt);

        rig.OpenPullRequest(7);
        var result = await Run(rig.Activities.RedriveAsync);

        var row = (await rig.Data.Pending.FindAsync(Repo, 7, Ct))!;
        Assert.Null(row.HeldAt);
        Assert.Equal(TriagePendingStatus.Redriven, row.Status);
        Assert.Equal(1, await rig.Data.Pending.CountOpenAsync(Ct));
        Assert.Equal(7, Assert.Single(rig.Signaler.Signals).PrNumber);
        Assert.Equal(1, result.Redriven);
    }

    [Fact]
    public async Task ARowSkippedForAnotherBlockingLabel_IsNotMarkedHeld()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7, TriageRules.RevertLabel);

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Null((await rig.Data.Pending.FindAsync(Repo, 7, Ct))!.HeldAt);
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(7));
        Assert.Equal(1, await rig.Data.Pending.CountOpenAsync(Ct));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task ARowThatSettles_IsNotMarkedHeld_EvenWhenItsPullRequestCarriesBlocked()
    {
        var rig = new Rig();
        await rig.ParkAsync(2);
        await rig.ParkAsync(3);
        rig.OpenPullRequest(2, TriageRules.BlockedLabel);
        rig.OpenPullRequest(3, TriageRules.NeedsReviewLabel, TriageRules.BlockedLabel);
        rig.Github.IssueEvents.Add(new GitHubIssueEvent("labeled", TriageRules.AllowedOwner, TriageRules.BlockedLabel, rig.Now.AddMinutes(5)));

        await Run(rig.Activities.RedriveAsync);

        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(2));
        Assert.Null((await rig.Data.Pending.FindAsync(Repo, 2, Ct))!.HeldAt);
        Assert.Equal(TriagePendingStatus.Owner, await rig.StatusAsync(3));
        Assert.Null((await rig.Data.Pending.FindAsync(Repo, 3, Ct))!.HeldAt);
    }

    [Fact]
    public async Task ARowWhosePullRequestIsAlreadyApproved_IsNotRedriven_AndDoesNotUseUpTheCap()
    {
        var rig = new Rig(perHour: 1);
        foreach (var pr in new[] { 1, 2 })
        {
            await rig.ParkAsync(pr);
            rig.OpenPullRequest(pr);
        }

        rig.Github.ReviewsByNumber[1] = [new GitHubReview(1, TriageRules.TriageBotLogin, "NONE", "APPROVED", Sha, rig.Now.AddHours(-1))];

        await Run(rig.Activities.RedriveAsync);

        Assert.Equal([2], rig.Signaler.Signals.Select(s => s.PrNumber));
    }

    [Fact]
    public async Task ARowWithAnUnappliedVerdict_IsRedriven_EvenWhenTheTriageBotAlreadyApproved()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Github.ReviewsByNumber[7] = [new GitHubReview(1, TriageRules.TriageBotLogin, "NONE", "APPROVED", Sha, rig.Now.AddHours(-1))];
        await rig.Data.Verdicts.RecordAsync(Repo, 7, Sha, new TriageDecision(TriageVerdict.Safe, "r", null), TriageMode.Live, null, Ct);

        await Run(rig.Activities.RedriveAsync);

        Assert.Equal(7, Assert.Single(rig.Signaler.Signals).PrNumber);
    }

    [Fact]
    public async Task ARowOfAnotherOwner_IsLeftAloneWithoutAnyGitHubCall()
    {
        var rig = new Rig();
        await rig.Data.Pending.UpsertAsync("someone-else/widgets", 7, Sha, "claude-pending", Ct);

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Empty(rig.Github.Calls);
    }

    [Fact]
    public async Task ARowThatSettledWhileTheRunWasStarting_IsNotMarkedRedriven()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Signaler.OnSignal = () => rig.Data.Pending.SetStatusAsync(Repo, 7, Sha, TriagePendingStatus.Settled, Ct);

        await Run(rig.Activities.RedriveAsync);

        Assert.Equal(TriagePendingStatus.Settled, await rig.StatusAsync(7));
    }

    [Theory]
    [InlineData("closed", false)]
    [InlineData("closed", true)]
    public async Task AClosedOrMergedPullRequest_IsNotRedriven(string state, bool merged)
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.Github.PullRequestsByNumber[7] = FakeGitHubApi.Renovate() with { State = state, Merged = merged };

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.OwnerHandled, await rig.StatusAsync(7));
    }

    [Fact]
    public async Task APullRequestWhoseTriageIsStillRunning_IsLeftAlone()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);
        rig.Probe.Running.Add((Repo, 7));

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(7));
        Assert.Equal(1, result.Running);
    }

    [Fact]
    public async Task InShadowMode_NothingIsRedriven()
    {
        var rig = new Rig(TriageMode.Shadow);
        await rig.ParkAsync(7);
        rig.OpenPullRequest(7);

        await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Empty(rig.Github.Calls);
    }

    [Fact]
    public async Task WhenGitHubFails_TheRowStaysQueuedForTheNextRun()
    {
        var rig = new Rig();
        await rig.ParkAsync(7);
        rig.Github.Failure = new GitHubApiException("boom", System.Net.HttpStatusCode.BadGateway);

        var result = await Run(rig.Activities.RedriveAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(TriagePendingStatus.Pending, await rig.StatusAsync(7));
        Assert.Equal(1, result.Errors);
    }

    private static HookDelivery Delivery(long id, string guid, TimeSpan ago, DateTimeOffset now, int status = 502, bool redelivery = false, string evt = "check_suite") =>
        new(id, guid, now - ago, redelivery, status, evt);

    [Fact]
    public async Task AFailedCheckSuiteDelivery_IsRedeliveredOnce_AndTheNewestDeliveryIsStored()
    {
        var rig = new Rig();
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromMinutes(5), rig.Now), Delivery(1, "g-1", TimeSpan.FromMinutes(20), rig.Now, status: 200)]);

        var result = await Run(rig.Activities.RedeliverAsync);

        Assert.Equal([2L], rig.App.Redelivered);
        Assert.Equal(rig.Now.AddMinutes(-5), (await rig.Sweeper.GetAsync(Ct)).LastSeenDeliveredAt);
        Assert.Equal(new RedeliverResult(1, 1, 1, 0), result);
    }

    [Fact]
    public async Task AGuidThatAlreadyHasASuccess_IsNotRedelivered()
    {
        var rig = new Rig();
        rig.App.Pages.Add([
            Delivery(3, "g-1", TimeSpan.FromMinutes(5), rig.Now, status: 200, redelivery: true),
            Delivery(1, "g-1", TimeSpan.FromMinutes(30), rig.Now, status: 502),
        ]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Empty(rig.App.Redelivered);
    }

    [Fact]
    public async Task AGuidAlreadyRedeliveredThreeTimes_IsLeftAlone()
    {
        var rig = new Rig();
        rig.App.Pages.Add([
            Delivery(4, "g-1", TimeSpan.FromMinutes(5), rig.Now, redelivery: true),
            Delivery(3, "g-1", TimeSpan.FromMinutes(15), rig.Now, redelivery: true),
            Delivery(2, "g-1", TimeSpan.FromMinutes(25), rig.Now, redelivery: true),
            Delivery(1, "g-1", TimeSpan.FromMinutes(35), rig.Now),
        ]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Empty(rig.App.Redelivered);
    }

    [Fact]
    public async Task AFailedRedelivery_IsRetriedByTheNextSweep_EvenWhenNewerDeliveriesMovedItsOriginalOutOfTheWindow()
    {
        var rig = new Rig();
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromMinutes(5), rig.Now, status: 200), Delivery(1, "g-1", TimeSpan.FromMinutes(30), rig.Now)]);
        await Run(rig.Activities.RedeliverAsync);

        rig.App.Pages.Clear();
        rig.App.Pages.Add([
            Delivery(3, "g-1", TimeSpan.FromMinutes(2), rig.Now, redelivery: true),
            Delivery(2, "g-2", TimeSpan.FromMinutes(5), rig.Now, status: 200),
            Delivery(1, "g-1", TimeSpan.FromMinutes(30), rig.Now),
        ]);
        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal([1L, 3L], rig.App.Redelivered);
    }

    [Fact]
    public async Task ARefusedRedelivery_IsTreatedAsDone_AndDoesNotFailTheStep()
    {
        var rig = new Rig();
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromMinutes(5), rig.Now), Delivery(1, "g-1", TimeSpan.FromMinutes(10), rig.Now)]);
        rig.App.Refused.Add(1);

        var result = await Run(rig.Activities.RedeliverAsync);

        Assert.Equal([1L, 2L], rig.App.Redelivered);
        Assert.Equal(new RedeliverResult(1, 2, 1, 1), result);
    }

    [Fact]
    public async Task OtherEventsAreNotRedelivered()
    {
        var rig = new Rig();
        rig.App.Pages.Add([Delivery(1, "g-1", TimeSpan.FromMinutes(5), rig.Now, evt: "ping")]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Empty(rig.App.Redelivered);
    }

    [Fact]
    public async Task PagingStops_AtTheFirstPageThatReachesBeforeTheWindow()
    {
        var rig = new Rig();
        rig.App.Pages.Add([Delivery(3, "g-3", TimeSpan.FromHours(1), rig.Now, status: 200)]);
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromDays(4), rig.Now, status: 200)]);
        rig.App.Pages.Add([Delivery(1, "g-1", TimeSpan.FromDays(5), rig.Now)]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal(2, rig.App.PagesRead);
        Assert.Empty(rig.App.Redelivered);
    }

    [Fact]
    public async Task WithADeliveryAlreadySeen_TheWindowStartsTenMinutesBeforeIt()
    {
        var rig = new Rig();
        await rig.Sweeper.SaveLastSeenAsync(rig.Now.AddHours(-1), Ct);
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromMinutes(65), rig.Now), Delivery(1, "g-1", TimeSpan.FromMinutes(75), rig.Now)]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal([2L], rig.App.Redelivered);
    }

    [Fact]
    public async Task WithNothingSeenForDays_TheWindowIsCappedAtThreeDays()
    {
        var rig = new Rig();
        await rig.Sweeper.SaveLastSeenAsync(rig.Now.AddDays(-10), Ct);
        rig.App.Pages.Add([Delivery(2, "g-2", TimeSpan.FromDays(2), rig.Now), Delivery(1, "g-1", TimeSpan.FromDays(4), rig.Now)]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal([2L], rig.App.Redelivered);
    }

    [Fact]
    public async Task PagingNeverPassesFiftyPages()
    {
        var rig = new Rig();
        for (var i = 0; i < 60; i++)
        {
            rig.App.Pages.Add([Delivery(i + 1, $"g-{i}", TimeSpan.FromMinutes(i + 1), rig.Now, status: 200)]);
        }

        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal(SweeperActivities.MaxDeliveryPages, rig.App.PagesRead);
    }

    [Fact]
    public async Task TheStoredDelivery_NeverMovesBackwards()
    {
        var rig = new Rig();
        await rig.Sweeper.SaveLastSeenAsync(rig.Now, Ct);
        rig.App.Pages.Add([Delivery(1, "g-1", TimeSpan.FromMinutes(5), rig.Now, status: 200)]);

        await Run(rig.Activities.RedeliverAsync);

        Assert.Equal(rig.Now, (await rig.Sweeper.GetAsync(Ct)).LastSeenDeliveredAt);
    }

    private static GitHubListedPullRequest Listed(int number, long? author = TriageRules.RenovateUserId, string? headRepo = Repo, params string[] labels) =>
        new(number, author, Sha, headRepo, Repo, labels);

    private static Rig ReconcileRig()
    {
        var rig = new Rig();
        rig.App.Repositories.Add(Repo);
        return rig;
    }

    [Fact]
    public async Task ARenovatePullRequestWithNoVerdictAndNoPendingRow_IsSignalled()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.Add(Listed(7));

        var result = await Run(rig.Activities.ReconcileAsync);

        Assert.Equal(7, Assert.Single(rig.Signaler.Signals).PrNumber);
        Assert.Equal(new ReconcileResult(1, 1, 0, 0), result);
    }

    [Fact]
    public async Task APullRequestWithAPendingRow_OrARunningTriage_OrABlockingLabel_OrAVerdict_IsNotSignalled()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.AddRange([Listed(1), Listed(2), Listed(3, labels: TriageRules.NeedsTriageLabel), Listed(4), Listed(5, labels: TriageRules.RevertLabel)]);
        await rig.Data.Pending.UpsertAsync(Repo, 1, Sha, "claude-pending", Ct);
        rig.Probe.Running.Add((Repo, 2));
        await rig.Data.Verdicts.RecordAsync(Repo, 4, Sha, new TriageDecision(TriageVerdict.Safe, "r", null), TriageMode.Live, null, Ct);
        await rig.Data.Verdicts.MarkAppliedAsync(Repo, 4, Sha, TriageVerdict.Safe, VerdictAction.Approve, Ct);

        await Run(rig.Activities.ReconcileAsync);

        Assert.Empty(rig.Signaler.Signals);
    }

    [Fact]
    public async Task APullRequestWhoseHeadAlreadyHasATriageCheck_IsNotSignalledAgain()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.Add(Listed(7));
        rig.OpenCheck(7, "neutral", "completed");

        await Run(rig.Activities.ReconcileAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal("neutral", Assert.Single(rig.Github.StoredCheckRuns).Run.Conclusion);
    }

    [Fact]
    public async Task APullRequestNotByRenovate_OrFromAFork_IsIgnored()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.AddRange([
            Listed(1, author: 99),
            Listed(2, headRepo: "someone/widgets"),
        ]);

        await Run(rig.Activities.ReconcileAsync);

        Assert.Empty(rig.Signaler.Signals);
    }

    [Fact]
    public async Task AnOpenTriageCheckWithNoPendingRowAndNoRunningTriage_IsCompletedAsFailure()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.Add(Listed(7));
        rig.OpenCheck(7);

        var result = await Run(rig.Activities.ReconcileAsync);

        var check = Assert.Single(rig.Github.StoredCheckRuns).Run;
        Assert.Equal(("completed", "failure"), (check.Status, check.Conclusion));
        Assert.Empty(rig.Signaler.Signals);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task WhenAReTriageStartedANewCheck_ThatNewestOpenCheckIsTheOneCompletedAsFailure()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.Add(Listed(7));
        rig.OpenCheck(7, "skipped", "completed");
        rig.OpenCheck(7);

        var result = await Run(rig.Activities.ReconcileAsync);

        Assert.Equal(["skipped", "failure"], rig.Github.StoredCheckRuns.Select(c => c.Run.Conclusion));
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task WhenTheNewestOfSeveralChecksIsCompleted_NothingIsSignalledOrWritten()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.Add(Listed(7));
        rig.OpenCheck(7, "skipped", "completed");
        rig.OpenCheck(7, "success", "completed");

        await Run(rig.Activities.ReconcileAsync);

        Assert.Empty(rig.Signaler.Signals);
        Assert.Empty(rig.Github.CheckRunWrites);
    }

    [Fact]
    public async Task AnOpenTriageCheckWithAPendingRow_OrARunningTriage_IsLeftOpen()
    {
        var rig = ReconcileRig();
        rig.Github.OpenPullRequests.AddRange([Listed(1), Listed(2) with { HeadSha = "b" + Sha[1..] }]);
        rig.OpenCheck(1);
        await rig.Data.Pending.UpsertAsync(Repo, 1, Sha, "claude-pending", Ct);
        rig.Github.StoredCheckRuns.Add((900, new GitHubCheckRunWrite(TriageCheckActivities.CheckName, "b" + Sha[1..], TriageCheckActivities.ExternalId(Repo, 2, "b" + Sha[1..]), "in_progress", null, TriageCheckActivities.CheckName, "s")));
        rig.Probe.Running.Add((Repo, 2));

        await Run(rig.Activities.ReconcileAsync);

        Assert.All(rig.Github.StoredCheckRuns, c => Assert.Equal("in_progress", c.Run.Status));
    }

    [Fact]
    public async Task AFailingRepository_IsCountedAndTheOthersStillRun()
    {
        var rig = ReconcileRig();
        rig.Github.Failure = new GitHubApiException("boom", System.Net.HttpStatusCode.BadGateway);

        var result = await Run(rig.Activities.ReconcileAsync);

        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task RecordingASuccess_StampsTheStoredTime()
    {
        var rig = new Rig();

        await new ActivityEnvironment().RunAsync(rig.Activities.RecordSuccessAsync);

        Assert.Equal(rig.Now, (await rig.Sweeper.GetAsync(Ct)).LastSuccessAt);
    }
}
