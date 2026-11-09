using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Livemap;

public class LivemapWakeActivitiesTests
{
    private const string Gid = "8886672487109033979";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly LivemapBuildWake Wake = new("1111111111111111111111111111111111111111", 77);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class FakeSignaler : ILivemapBuildSignaler
    {
        public List<(long BuildId, LivemapBuildWake Wake)> Signalled { get; } = [];

        public bool Delivered { get; set; } = true;

        public Exception? Failure { get; set; }

        public int FailFirstCalls { get; set; }

        private int _calls;

        public Task<bool> SignalAsync(long buildId, LivemapBuildWake wake, CancellationToken cancellationToken)
        {
            _calls++;
            if (Failure is not null && (FailFirstCalls == 0 || _calls <= FailFirstCalls))
            {
                return Task.FromException<bool>(Failure);
            }

            Signalled.Add((buildId, wake));
            return Task.FromResult(Delivered);
        }
    }

    private sealed class Rig
    {
        public Rig()
        {
            Store = new LivemapBuildStore(InMemoryRunData.Create().Factory, new FixedTimeProvider(Now));
            Activities = new LivemapWakeActivities(Store, Signaler, GitHub, NullLogger<LivemapWakeActivities>.Instance);
        }

        public LivemapBuildStore Store { get; }

        public FakeSignaler Signaler { get; } = new();

        public FakeLivemapGitHub GitHub { get; } = new();

        public LivemapWakeActivities Activities { get; }

        public Task<LivemapWakeOutcome> WakeAsync() => new ActivityEnvironment().RunAsync(() => Activities.WakeAsync(Wake));

        public Task<LivemapBuild> SeedAsync(long buildId, LivemapBuildState state) =>
            Store.RecordAsync(buildId, Gid, Now, state, Ct);
    }

    [Fact]
    public async Task WithNoBuildInFlight_NothingIsSignalled()
    {
        var rig = new Rig();

        Assert.Equal(LivemapWakeOutcome.NoBuildInFlight, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Theory]
    [InlineData(LivemapBuildState.Done)]
    [InlineData(LivemapBuildState.Parked)]
    [InlineData(LivemapBuildState.PullRequestWaiting)]
    [InlineData(LivemapBuildState.Merged)]
    [InlineData(LivemapBuildState.NeedsOwner)]
    [InlineData(LivemapBuildState.Superseded)]
    public async Task ABuildThatIsNotInFlight_IsNotSignalled(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.SeedAsync(400, state);

        Assert.Equal(LivemapWakeOutcome.NoBuildInFlight, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Theory]
    [InlineData(LivemapBuildState.Classified)]
    [InlineData(LivemapBuildState.PullRequestOpen)]
    [InlineData(LivemapBuildState.Merging)]
    public async Task ABuildWithItsPullRequestToWatch_IsSignalled(LivemapBuildState state)
    {
        var rig = new Rig();
        await rig.SeedAsync(400, state);

        Assert.Equal(LivemapWakeOutcome.Woken, await rig.WakeAsync());
        Assert.Equal(400L, Assert.Single(rig.Signaler.Signalled).BuildId);
    }

    [Fact]
    public async Task TheBuildInFlight_IsSignalledWithTheWake()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);

        var outcome = await rig.WakeAsync();

        Assert.Equal(LivemapWakeOutcome.Woken, outcome);
        Assert.Equal((400L, Wake), Assert.Single(rig.Signaler.Signalled));
    }

    [Fact]
    public async Task TheRunsOfTheWakesSuite_AreLookedUp_BeforeTheBuildIsSignalled()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);

        await rig.WakeAsync();

        Assert.Equal(Wake.SuiteId, Assert.Single(rig.GitHub.SuiteLookups));
    }

    [Fact]
    public async Task ADispatchRunOfTheFork_SignalsTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [new LivemapSuiteRun("workflow_dispatch", "main", FakeLivemapGitHub.ForkId, FakeLivemapGitHub.ForkId)];

        Assert.Equal(LivemapWakeOutcome.Woken, await rig.WakeAsync());
        Assert.Single(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task ABuildPullRequestRunInTheFork_SignalsTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [new LivemapSuiteRun("pull_request", "livemap/build-26430253", FakeLivemapGitHub.ForkId, FakeLivemapGitHub.ForkId)];

        Assert.Equal(LivemapWakeOutcome.Woken, await rig.WakeAsync());
        Assert.Single(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task ARunWhoseHeadIsAnotherRepository_DoesNotSignalTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [new LivemapSuiteRun("workflow_dispatch", "main", FakeLivemapGitHub.ForkId, 900)];

        Assert.Equal(LivemapWakeOutcome.NotBuildRun, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("livemap/build-26430253")]
    public async Task APullRequestRunFromAnotherRepository_DoesNotSignalTheBuild(string branch)
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [new LivemapSuiteRun("pull_request", branch, FakeLivemapGitHub.ForkId, 900)];

        Assert.Equal(LivemapWakeOutcome.NotBuildRun, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task APullRequestRunInTheForkFromOutsideTheBuildBranches_DoesNotSignalTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [new LivemapSuiteRun("pull_request", "main", FakeLivemapGitHub.ForkId, FakeLivemapGitHub.ForkId)];

        Assert.Equal(LivemapWakeOutcome.NotBuildRun, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task ASuiteWithOneRunOfAnotherRepositoryAmongTheForks_DoesNotSignalTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns =
        [
            new LivemapSuiteRun("workflow_dispatch", "main", FakeLivemapGitHub.ForkId, FakeLivemapGitHub.ForkId),
            new LivemapSuiteRun("workflow_dispatch", "main", FakeLivemapGitHub.ForkId, 900),
        ];

        Assert.Equal(LivemapWakeOutcome.NotBuildRun, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task ASuiteWithNoRun_DoesNotSignalTheBuild()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRuns = [];

        Assert.Equal(LivemapWakeOutcome.NoRunForSuite, await rig.WakeAsync());
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task WhenTheRunLookupFails_TheActivityFails_SoItIsRetried_AndTheBuildIsNotSignalled()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.GitHub.SuiteRunsFailure = new GitHubApiException("GitHub workflow run list failed with status 502.", System.Net.HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<GitHubApiException>(rig.WakeAsync);
        Assert.Empty(rig.Signaler.Signalled);
    }

    [Fact]
    public async Task WithNoBuildInFlight_NoRunIsLookedUp()
    {
        var rig = new Rig();

        await rig.WakeAsync();

        Assert.Empty(rig.GitHub.SuiteLookups);
    }

    [Fact]
    public async Task OnlyTheNewestBuildInFlight_IsSignalled()
    {
        var rig = new Rig();
        await rig.SeedAsync(300, LivemapBuildState.Detected);
        await rig.SeedAsync(400, LivemapBuildState.Detected);

        await rig.WakeAsync();

        Assert.Equal(400, Assert.Single(rig.Signaler.Signalled).BuildId);
    }

    [Fact]
    public async Task WhenNoWorkflowIsRunningForTheBuild_TheWakeEndsQuietly()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.Signaler.Delivered = false;

        Assert.Equal(LivemapWakeOutcome.NotRunning, await rig.WakeAsync());
    }

    [Fact]
    public async Task WhenTheSignalFails_TheActivityFails_SoItIsRetried()
    {
        var rig = new Rig();
        await rig.SeedAsync(400, LivemapBuildState.Detected);
        rig.Signaler.Failure = new InvalidOperationException("Temporal is down");

        await Assert.ThrowsAsync<InvalidOperationException>(rig.WakeAsync);
    }
}
