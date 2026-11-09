using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapNoticeTests
{
    private const string Fork = "octo/livemap";

    private const string PinRepo = "octo/cluster";

    private static readonly DateTimeOffset Detected = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static LivemapBuild Shipped() => new()
    {
        BuildId = 777,
        State = LivemapBuildState.Pinned,
        CompatRunId = 9002,
        PullRequestNumber = 41,
        ReleaseTag = "v2.0.0",
        PinPullRequestNumber = 88,
        CreatedAt = Detected,
        MergedAt = Detected.AddMinutes(62),
        ReleasedAt = Detected.AddHours(26).AddMinutes(5),
        PinnedAt = Detected.AddHours(27),
    };

    [Fact]
    public void TheMarker_NamesTheBuild_AndIsLeftToTheClientThatPostsTheNotice()
    {
        var text = LivemapNotice.Compose(Shipped(), Fork, PinRepo);

        Assert.DoesNotContain("livemap-build", text, StringComparison.Ordinal);
        Assert.NotEqual(LivemapNotice.Marker(777), LivemapNotice.Marker(7770));
    }

    [Fact]
    public void AShippedBuild_LinksTheCompatRun_TheDataPullRequest_TheRelease_AndThePinPullRequest()
    {
        var text = LivemapNotice.Compose(Shipped(), Fork, PinRepo);

        Assert.Contains("shipped", text, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/actions/runs/9002", text, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/pull/41", text, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/livemap/releases/tag/v2.0.0", text, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/cluster/pull/88", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AShippedBuild_ReportsTheTimingsFromDetection()
    {
        var text = LivemapNotice.Compose(Shipped(), Fork, PinRepo);

        Assert.Contains("Detected: 2026-11-09 08:00 UTC", text, StringComparison.Ordinal);
        Assert.Contains("Data pull request merged: 1h 02m after detection", text, StringComparison.Ordinal);
        Assert.Contains("Released: 26h 05m after detection", text, StringComparison.Ordinal);
        Assert.Contains("Pinned: 27h 00m after detection", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkOrTimingThatDoesNotExist_IsLeftOut()
    {
        var build = Shipped();
        build.PinPullRequestNumber = null;
        build.ReleaseTag = null;
        build.CompatRunId = null;
        build.MergedAt = null;
        build.ReleasedAt = null;
        build.PinnedAt = null;

        var text = LivemapNotice.Compose(build, Fork, PinRepo);

        Assert.DoesNotContain("Pin pull request", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Release:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compat run", text, StringComparison.Ordinal);
        Assert.DoesNotContain("merged:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Released:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pinned:", text, StringComparison.Ordinal);
        Assert.Contains("Detected:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildThatNeededNoPinPullRequest_SaysTheReleaseWasAlreadyPinned()
    {
        var build = Shipped();
        build.PinPullRequestNumber = null;

        var text = LivemapNotice.Compose(build, Fork, PinRepo);

        Assert.Contains("already", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildThatNeedsAPerson_SaysSo_AndCarriesTheReason()
    {
        var build = Shipped();
        build.State = LivemapBuildState.NeedsOwner;
        build.PinPullRequestNumber = null;
        build.PinnedAt = null;
        build.Detail = "The release did not pass its checks.";

        var text = LivemapNotice.Compose(build, Fork, PinRepo);

        Assert.Contains("needs a person", text, StringComparison.Ordinal);
        Assert.Contains("The release did not pass its checks.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildThatNeedsAPerson_WithoutAReason_StillSaysWhatToDo()
    {
        var build = Shipped();
        build.State = LivemapBuildState.NeedsOwner;
        build.Detail = null;

        var text = LivemapNotice.Compose(build, Fork, PinRepo);

        Assert.Contains("needs a person", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LivemapBuildState.Detected)]
    [InlineData(LivemapBuildState.Merged)]
    [InlineData(LivemapBuildState.Done)]
    public void OnlyAnEndState_GetsANotice(LivemapBuildState state)
    {
        var build = Shipped();
        build.State = state;

        Assert.Throws<ArgumentException>(() => LivemapNotice.Compose(build, Fork, PinRepo));
    }
}
