using System.Globalization;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public sealed class LivemapShipActivities(
    IOptions<LivemapOptions> options,
    LivemapBuildStore builds,
    ILivemapPullRequests forkPulls,
    ILivemapPinPullRequests pinPulls,
    ILivemapGitHub github,
    LivemapReleaseVerifier verifier,
    ILivemapReleases releases,
    ILivemapNotices notices,
    TimeProvider time)
{
    public const string ShipName = "LivemapBuildShip";

    public static readonly TimeSpan ReleaseLimit = TimeSpan.FromHours(6);

    public static readonly TimeSpan PublishLimit = TimeSpan.FromHours(2);

    private const int PinMaxAttempts = 2;

    private enum MergeOutcome
    {
        Merged,
        MainMoved,
        Refused,
    }

    // True while the step waits on something outside the worker; any other answer means the state moved on and the plan is read again.
    [Activity(ShipName)]
    public async Task<bool> ShipAsync(long buildId)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var row = await builds.FindAsync(buildId, cancellationToken).ConfigureAwait(false);
        return row?.State switch
        {
            LivemapBuildState.Merged => await MergeReleaseAsync(row, cancellationToken).ConfigureAwait(false),
            LivemapBuildState.Released => await VerifyReleaseAsync(row, cancellationToken).ConfigureAwait(false),
            LivemapBuildState.Verified => await OpenPinAsync(row, cancellationToken).ConfigureAwait(false),
            LivemapBuildState.PinOpen => await MergePinAsync(row, cancellationToken).ConfigureAwait(false),
            LivemapBuildState.Pinned or LivemapBuildState.NeedsOwner or LivemapBuildState.OwnerReview when row.NotifiedAt is null => await NotifyAsync(row, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }

    // A release that is already out counts whoever merged its pull request, so a merge whose answer was lost is picked up here.
    private async Task<bool> MergeReleaseAsync(LivemapBuild row, CancellationToken cancellationToken)
    {
        if (await verifier.FindReleaseWithBuildAsync(row.BuildId, cancellationToken).ConfigureAwait(false) is { } published)
        {
            await builds.ReleasedAsync(row.BuildId, published.Tag, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!options.Value.AutoMerge)
        {
            await builds.HoldAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var pulls = await forkPulls.FindOpenByBranchAsync(LivemapReleaseGate.Branch, cancellationToken).ConfigureAwait(false);
        if (pulls is not [var pull])
        {
            return await WaitAsync(row, ReleaseLimit, pulls.Count == 0 ? "No release pull request is open." : "More than one release pull request is open.", cancellationToken).ConfigureAwait(false);
        }

        var mainHead = await forkPulls.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var verdict = LivemapReleaseGate.Evaluate(await ReadReleaseFactsAsync(pull, cancellationToken).ConfigureAwait(false), options.Value.ReleaseRequiredChecks);
        if (verdict.Outcome != LivemapGateOutcome.Passed)
        {
            return await WaitAsync(row, ReleaseLimit, Describe(verdict), cancellationToken).ConfigureAwait(false);
        }

        var outcome = await MergeAsync(forkPulls, pull, mainHead, cancellationToken).ConfigureAwait(false);
        if (outcome != MergeOutcome.Merged)
        {
            return await WaitAsync(row, ReleaseLimit, outcome == MergeOutcome.MainMoved ? "Main moved on before the release pull request was merged." : "GitHub refused to merge the release pull request.", cancellationToken).ConfigureAwait(false);
        }

        await builds.ReleasedAsync(row.BuildId, $"v{LivemapReleaseGate.VersionOf(pull.Title)}", cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<LivemapReleaseFacts> ReadReleaseFactsAsync(LivemapPullRequest pull, CancellationToken cancellationToken)
    {
        var commit = await forkPulls.GetCommitAsync(pull.HeadSha, cancellationToken).ConfigureAwait(false);
        var version = await forkPulls.ReadFileAsync(pull.HeadSha, LivemapReleaseGate.VersionPath, cancellationToken).ConfigureAwait(false);
        var checkRuns = await forkPulls.ListCheckRunsAsync(pull.HeadSha, cancellationToken).ConfigureAwait(false);
        return new LivemapReleaseFacts(
            Pull: pull,
            Fork: options.Value.Repo,
            ReleaseBot: options.Value.ReleaseBot,
            HeadParents: commit.Parents,
            Files: commit.Files,
            VersionAtHead: version,
            CheckRuns: checkRuns,
            CiSuiteIds: await LivemapCiSuites.FindAsync(github, checkRuns, LivemapReleaseGate.Branch, options.Value.ReleaseRequiredChecks, cancellationToken).ConfigureAwait(false));
    }

    private async Task<bool> VerifyReleaseAsync(LivemapBuild row, CancellationToken cancellationToken)
    {
        if (row.ReleaseTag is not { } tag)
        {
            await FlagAsync(row, "The build has no release recorded.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var check = await verifier.VerifyAsync(tag, row.BuildId, cancellationToken).ConfigureAwait(false);
        switch (check)
        {
            case { Outcome: LivemapReleaseCheckOutcome.Verified, Sha256: { } sha256 }:
                await builds.VerifiedAsync(row.BuildId, sha256, cancellationToken).ConfigureAwait(false);
                return false;
            case { Outcome: LivemapReleaseCheckOutcome.Pending }:
                return await AwaitPublishAsync(row, tag, check.Reason ?? "The release is not ready.", cancellationToken).ConfigureAwait(false);
            default:
                await FlagAsync(row, check.Reason ?? "The release did not pass its check.", cancellationToken).ConfigureAwait(false);
                return false;
        }
    }

    // A failed publish run gets its failed jobs run once more; a second failure is for a person.
    private async Task<bool> AwaitPublishAsync(LivemapBuild row, string tag, string reason, CancellationToken cancellationToken)
    {
        var run = await releases.GetTagRunAsync(tag, cancellationToken).ConfigureAwait(false);
        if (run is not { Status: "completed" } || run.Conclusion == "success")
        {
            return await WaitAsync(row, PublishLimit, reason, cancellationToken).ConfigureAwait(false);
        }

        if (run.Attempt >= 2)
        {
            await FlagAsync(row, $"The release workflow failed twice: {run.Url}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        await releases.RerunFailedJobsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> OpenPinAsync(LivemapBuild row, CancellationToken cancellationToken)
    {
        if (row is not { ReleaseTag: { } tag, ReleaseSha256: { } sha256 })
        {
            await FlagAsync(row, "The build has no verified release recorded.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var pinPath = options.Value.PinPath;
        var mainHead = await pinPulls.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var pin = await pinPulls.ReadFileAsync(mainHead, pinPath, cancellationToken).ConfigureAwait(false);
        if (pin is null)
        {
            await FlagAsync(row, "The pin file could not be read.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (LivemapPin.ReadVersion(pin) is { } pinned && !LivemapPin.IsAhead(tag, pinned))
        {
            await builds.PinnedAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (LivemapPin.Write(pin, tag, sha256) is not { } content)
        {
            await FlagAsync(row, "The pin file does not hold the version and digest lines the worker edits.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var branch = LivemapPinBranch.For(tag);
        var headline = $"chore(enshrouded): update livemap to {tag}";
        await LivemapBranches.RemoveAsync(pinPulls, branch, cancellationToken).ConfigureAwait(false);
        await pinPulls.CreateBranchAsync(branch, mainHead, cancellationToken).ConfigureAwait(false);
        var head = await pinPulls.CommitFileAsync(branch, mainHead, headline, pinPath, content, cancellationToken).ConfigureAwait(false);
        var number = await pinPulls.OpenAsync(branch, headline, PinBody(tag), cancellationToken).ConfigureAwait(false);
        if (!await builds.OpenPinAsync(row.BuildId, number, head, waiting: !options.Value.AutoMerge, cancellationToken).ConfigureAwait(false))
        {
            await CloseAsync(number, branch, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    // The tracking issue is in the fork, so it is linked by its full address: a bare number would link an issue of the pin repository.
    private string PinBody(string tag) =>
        $"Pins the Livemap release.\n\nRelease: https://github.com/{options.Value.Repo}/releases/tag/{tag}\nTracking issue: https://github.com/{options.Value.Repo}/issues/{options.Value.TrackingIssue.ToString(CultureInfo.InvariantCulture)}\n";

    private async Task<bool> MergePinAsync(LivemapBuild row, CancellationToken cancellationToken)
    {
        if (row is not { PinPullRequestNumber: { } number, PinHeadSha: { } head, ReleaseTag: { } tag })
        {
            await FlagAsync(row, "The build has no pin pull request recorded.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var pull = await pinPulls.GetAsync(number, cancellationToken).ConfigureAwait(false);
        if (pull is null || (pull.State != "open" && !pull.Merged))
        {
            await FlagAsync(row, pull is null ? "The pin pull request could not be found." : "The pin pull request was closed without being merged.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (pull.Merged)
        {
            await SettleMergedAsync(row, pull, head, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!options.Value.AutoMerge)
        {
            await builds.HoldAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var branch = LivemapPinBranch.For(tag);
        var mainHead = await pinPulls.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var facts = new LivemapPinFacts(
            Pull: pull,
            PinRepo: options.Value.PinRepo,
            PinPath: options.Value.PinPath,
            Branch: branch,
            ExpectedHeadSha: head,
            AppLogin: await pinPulls.GetAppLoginAsync(cancellationToken).ConfigureAwait(false),
            MainHead: mainHead,
            HeadParents: await pinPulls.GetParentsAsync(pull.HeadSha, cancellationToken).ConfigureAwait(false),
            Files: await pinPulls.ListFilesAsync(number, cancellationToken).ConfigureAwait(false));
        var verdict = LivemapPinGate.Evaluate(facts);
        if (verdict.Outcome == LivemapGateOutcome.Refused)
        {
            await CloseAsync(number, branch, cancellationToken).ConfigureAwait(false);
            await FlagAsync(row, verdict.Reason!, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var outcome = verdict.Outcome == LivemapGateOutcome.MainMoved ? MergeOutcome.MainMoved : await MergeAsync(pinPulls, pull, mainHead, cancellationToken).ConfigureAwait(false);
        if (outcome == MergeOutcome.Merged)
        {
            await builds.PinnedAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await CloseAsync(number, branch, cancellationToken).ConfigureAwait(false);
        await builds.RedoPinAsync(row.BuildId, outcome == MergeOutcome.MainMoved ? "Main moved on before the pin pull request was merged." : "GitHub refused to merge the pin pull request.", PinMaxAttempts, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task SettleMergedAsync(LivemapBuild row, LivemapPullRequest pull, string head, CancellationToken cancellationToken)
    {
        if (pull.HeadSha == head)
        {
            await builds.PinnedAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await FlagAsync(row, "The pin pull request was merged at another commit than the one the worker made.", cancellationToken).ConfigureAwait(false);
        }
    }

    // GitHub merges only the head given; an answer lost on the way is found again by the pull request being merged at that head.
    private async Task<MergeOutcome> MergeAsync(ILivemapPullRequests repository, LivemapPullRequest pull, string mainHead, CancellationToken cancellationToken)
    {
        var title = $"{pull.Title} (#{pull.Number.ToString(CultureInfo.InvariantCulture)})";
        for (var retry = 0; ; retry++)
        {
            if (await repository.GetMainHeadAsync(cancellationToken).ConfigureAwait(false) != mainHead)
            {
                return MergeOutcome.MainMoved;
            }

            var result = await repository.MergeAsync(pull.Number, pull.HeadSha, title, cancellationToken).ConfigureAwait(false);
            if (result == LivemapMergeResult.Merged)
            {
                return MergeOutcome.Merged;
            }

            var after = await repository.GetAsync(pull.Number, cancellationToken).ConfigureAwait(false);
            if (after is { Merged: true } && after.HeadSha == pull.HeadSha)
            {
                return MergeOutcome.Merged;
            }

            if (result != LivemapMergeResult.NotMergeable || retry >= LivemapPullRequestActivities.MergeRetries)
            {
                return MergeOutcome.Refused;
            }

            await Task.Delay(LivemapPullRequestActivities.MergeRetryWait, time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Each end state is told to the fork's tracking issue once; the post finds an earlier one by its marker, so a retry never repeats it.
    private async Task<bool> NotifyAsync(LivemapBuild row, CancellationToken cancellationToken)
    {
        var appLogin = await forkPulls.GetAppLoginAsync(cancellationToken).ConfigureAwait(false);
        var body = LivemapNotice.Compose(row, options.Value.Repo, options.Value.PinRepo);
        await notices.PostOnceAsync(row.BuildId, appLogin, row.CreatedAt, body, cancellationToken).ConfigureAwait(false);
        await builds.NotifiedAsync(row.BuildId, cancellationToken).ConfigureAwait(false);
        return false;
    }

    // Waiting is the answer until the limit passes; then a person is asked, with the reason the step could not go on.
    private async Task<bool> WaitAsync(LivemapBuild row, TimeSpan limit, string reason, CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - row.UpdatedAt <= limit)
        {
            return true;
        }

        await FlagAsync(row, reason, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task FlagAsync(LivemapBuild row, string detail, CancellationToken cancellationToken) =>
        await builds.FlagPullRequestAsync(row.BuildId, detail, cancellationToken).ConfigureAwait(false);

    private async Task CloseAsync(int number, string branch, CancellationToken cancellationToken)
    {
        await pinPulls.ClosePullRequestAsync(number, cancellationToken).ConfigureAwait(false);
        await pinPulls.DeleteBranchAsync(branch, cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(LivemapGateVerdict verdict) => verdict.Outcome switch
    {
        LivemapGateOutcome.Pending => $"Required checks have not finished: {string.Join(", ", verdict.Names)}.",
        LivemapGateOutcome.Failed => $"Required checks failed: {string.Join(", ", verdict.Names)}.",
        _ => verdict.Reason!,
    };
}
