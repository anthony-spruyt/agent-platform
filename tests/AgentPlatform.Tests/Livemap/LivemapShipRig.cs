using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

// A build merged in the fork, a release the fork can publish and a pin file in the cluster repository, all set up so that every step passes until a test changes one fact.
internal sealed class LivemapShipRig
{
    public const string Tag = "v2.0.0";
    public const string OldTag = "v1.9.1";
    public const string PinRepo = "octo/cluster";
    public const string PinPath = "apps/livemap/pin.yaml";
    public const string ReleaseBot = "release-bot[bot]";
    public const string PinTitle = "chore(enshrouded): update livemap to v2.0.0";
    public const int ReleaseNumber = 61;
    public const int PinNumber = 88;
    public const long ReleaseSuiteId = 8200;
    public const long TagRunId = 5100;

    public static readonly string ReleaseHead = new('c', 40);
    public static readonly string PinHead = new('7', 40);
    public static readonly string OldDigest = new('b', 64);

    public static readonly string[] ReleaseChecks = ["lint / MegaLinter", "repo / Build, test, package", "summary / Check Results"];

    public static readonly string PinFile = $"# renovate: datasource=github-release-attachments depName=octo/livemap digest=sha256\nLIVEMAP_VERSION: \"{OldTag}\"\nLIVEMAP_SHA256: \"{OldDigest}\"\n";

    private const string CMakePatch = "@@ -3,7 +3,7 @@\n-project(live_map VERSION 1.9.1 LANGUAGES CXX) # x-release-please-version\n+project(live_map VERSION 2.0.0 LANGUAGES CXX) # x-release-please-version";

    public LivemapShipRig()
    {
        Data = InMemoryRunData.Create();
        Store = new LivemapBuildStore(Data.Factory, new FixedTimeProvider(Now));
        GitHub.ForkBuilds = KnownArtifact().BuildsJson;
        Pin.Contents[(Pin.MainHead, PinPath)] = PinFile;
    }

    public InMemoryRunData Data { get; }

    public LivemapBuildStore Store { get; }

    public FakeLivemapGitHub GitHub { get; } = new();

    public FakeLivemapReleases Releases { get; } = new();

    public FakeLivemapPullRequests Fork { get; } = new();

    public FakeLivemapPullRequests Pin { get; } = new() { Repo = PinRepo, OpenedFile = PinPath };

    public FakeLivemapNotices Notices { get; } = new();

    public LivemapOptions Config { get; } = new()
    {
        Enabled = true,
        Repo = FakeLivemapPullRequests.Fork,
        PinRepo = PinRepo,
        PinPath = PinPath,
        TrackingIssue = TrackingIssue,
        AutoMerge = true,
        ReleaseBot = ReleaseBot,
        ReleaseRequiredChecks = [.. ReleaseChecks],
    };

    public InstantTimeProvider Time { get; } = new(Now);

    public static InstantTimeProvider After(TimeSpan span) => new(Now + span);

    public LivemapShipActivities Activities(TimeProvider? time = null) =>
        new(Options.Create(Config), Store, Fork, Pin, GitHub, new LivemapReleaseVerifier(Releases, GitHub, Options.Create(Config)), Releases, Notices, time ?? Time);

    public Task<bool> ShipAsync(TimeProvider? time = null) =>
        new ActivityEnvironment().RunAsync(() => Activities(time).ShipAsync(BuildId));

    public async Task<LivemapBuild> RowAsync() => (await Store.FindAsync(BuildId, Ct))!;

    public async Task<LivemapBuild> AtAsync(LivemapBuildState state)
    {
        await Store.RecordAsync(BuildId, Gid, SteamUpdated, LivemapBuildState.Detected, Ct);
        Assert.True(await Store.ClassifyAsync(BuildId, LivemapClassification.Known, FakeLivemapGitHub.CompatRunId, Ct));
        Assert.True(await Store.OpenPullRequestAsync(BuildId, 41, Sha, waiting: false, Ct));
        Assert.True(await Store.BeginMergeAsync(BuildId, Ct));
        Assert.True(await Store.MergedAsync(BuildId, Ct));
        await ThroughAsync(state, LivemapBuildState.Released, () => Store.ReleasedAsync(BuildId, Tag, Ct));
        await ThroughAsync(state, LivemapBuildState.Verified, () => Store.VerifiedAsync(BuildId, FakeLivemapReleases.ZipSha, Ct));
        await ThroughAsync(state, LivemapBuildState.PinOpen, () => Store.OpenPinAsync(BuildId, PinNumber, PinHead, waiting: false, Ct));
        await ThroughAsync(state, LivemapBuildState.Pinned, () => Store.PinnedAsync(BuildId, Ct));
        var row = await RowAsync();
        Assert.Equal(state, row.State);
        return row;
    }

    public LivemapPullRequest SeedReleasePullRequest()
    {
        var pull = new LivemapPullRequest(ReleaseNumber, "open", false, false, "main", FakeLivemapPullRequests.Fork, LivemapReleaseGate.Branch, FakeLivemapPullRequests.Fork, ReleaseHead, ReleaseBot, "Bot", 1, "chore(main): release 2.0.0");
        Fork.Seed(
            pull,
            new LivemapPullRequestFile(".release-please-manifest.json", "modified", null, "@@ -1 +1 @@"),
            new LivemapPullRequestFile("CHANGELOG.md", "modified", null, "@@ -1 +1,5 @@"),
            new LivemapPullRequestFile("CMakeLists.txt", "modified", null, CMakePatch),
            new LivemapPullRequestFile("version.txt", "modified", null, "@@ -1 +1 @@\n-1.9.1\n+2.0.0"));
        Fork.Parents[ReleaseHead] = [Fork.MainHead];
        Fork.Contents[(ReleaseHead, "version.txt")] = "2.0.0\n";
        Fork.Checks[ReleaseHead] = [.. ReleaseChecks.Select(name => CheckRun(name, suite: ReleaseSuiteId))];
        GitHub.SuiteRunsBySuite[ReleaseSuiteId] = [CiRun(branch: LivemapReleaseGate.Branch)];
        return pull;
    }

    public LivemapPullRequest SeedPinPullRequest()
    {
        var branch = LivemapPinBranch.For(Tag);
        var pull = new LivemapPullRequest(PinNumber, "open", false, false, "main", PinRepo, branch, PinRepo, PinHead, FakeLivemapPullRequests.AppLogin, "Bot", 1, PinTitle);
        Pin.Seed(pull, new LivemapPullRequestFile(PinPath, "modified", null, "@@ -1 +1 @@"));
        Pin.Branches[branch] = PinHead;
        Pin.Parents[PinHead] = [Pin.MainHead];
        return pull;
    }

    public void PublishRelease() => Releases.Seed(Tag);

    private static async Task ThroughAsync(LivemapBuildState wanted, LivemapBuildState step, Func<Task<bool>> move)
    {
        if (StepOrder.IndexOf(step) > StepOrder.IndexOf(wanted))
        {
            return;
        }

        Assert.True(await move());
    }

    private static readonly List<LivemapBuildState> StepOrder =
    [
        LivemapBuildState.Merged,
        LivemapBuildState.Released,
        LivemapBuildState.Verified,
        LivemapBuildState.PinOpen,
        LivemapBuildState.Pinned,
    ];
}
