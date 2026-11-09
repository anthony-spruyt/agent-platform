using System.Globalization;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapStep
{
    Stop,
    Check,
    OpenPullRequest,
    Watch,
}

// Outcome is what the workflow ends with when the step is Stop; the head is the commit a Watch waits on.
public sealed record LivemapPlan(LivemapStep Step, LivemapBuildOutcome Outcome = LivemapBuildOutcome.Classified, string? HeadSha = null);

public sealed class LivemapPullRequestActivities(
    IOptions<LivemapOptions> options,
    LivemapBuildStore builds,
    ILivemapPullRequests pullRequests,
    ILivemapGitHub github,
    ILivemapBuildsSchema schema,
    TimeProvider time)
{
    public static readonly TimeSpan ChecksLimit = TimeSpan.FromHours(3);

    public static readonly TimeSpan MergeRetryWait = TimeSpan.FromSeconds(5);

    public const string PlanName = "LivemapBuildPlan";
    public const string OpenPullRequestName = "LivemapBuildOpenPullRequest";
    public const string AdvanceName = "LivemapBuildAdvance";

    private const int MergeRetries = 3;

    private const string MainMovedDetail = "Main moved on before the pull request was merged.";

    [Activity(PlanName)]
    public async Task<LivemapPlan> PlanAsync(long buildId)
    {
        var row = await builds.FindAsync(buildId, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
        return row?.State switch
        {
            LivemapBuildState.Detected => new LivemapPlan(LivemapStep.Check),
            LivemapBuildState.Classified => new LivemapPlan(LivemapStep.OpenPullRequest),
            LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging => new LivemapPlan(LivemapStep.Watch, HeadSha: row.HeadSha),
            LivemapBuildState.PullRequestWaiting => new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.PullRequestWaiting),
            LivemapBuildState.Merged => new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.Merged),
            LivemapBuildState.NeedsOwner => new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.NeedsOwner),
            LivemapBuildState.Parked => new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.Parked),
            _ => new LivemapPlan(LivemapStep.Stop, LivemapBuildOutcome.Superseded),
        };
    }

    // Each run starts from a clean branch, so an attempt that died half way is repeated whole and a retry never meets its own leftovers.
    [Activity(OpenPullRequestName)]
    public async Task OpenPullRequestAsync(long buildId)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var row = await builds.FindAsync(buildId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: LivemapBuildState.Classified })
        {
            return;
        }

        if (row.Classification is not { } classification || row.CompatRunId is not { } runId)
        {
            await StartOverAsync(buildId, "The build was classified without the compat run that decided it.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var branch = LivemapBuildBranch.For(buildId);
        await RemoveAsync(branch, cancellationToken).ConfigureAwait(false);
        CompatArtifact? artifact;
        try
        {
            artifact = await github.ReadCompatArtifactAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (CompatArtifactException ex)
        {
            await builds.FlagPullRequestAsync(buildId, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (artifact?.BuildsJson is not { } proposed)
        {
            await StartOverAsync(buildId, "The compat run's artifact is no longer there.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var mainHead = await pullRequests.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var current = await github.ReadBuildsJsonAsync(mainHead, cancellationToken).ConfigureAwait(false);
        var schemaJson = await github.ReadBuildsSchemaAsync(mainHead, cancellationToken).ConfigureAwait(false);
        var decided = CompatClassifier.Classify(artifact, current, buildId, row.ManifestGid);
        if (decided.Kind != (classification == LivemapClassification.Known ? CompatOutcomeKind.Known : CompatOutcomeKind.Easy))
        {
            await StartOverAsync(buildId, $"Main changed since the compat run: {decided.Detail}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var problem = schemaJson is null ? "The fork has no builds schema on main." : schema.Check(schemaJson, proposed);
        if (problem is not null)
        {
            await builds.FlagPullRequestAsync(buildId, problem, cancellationToken).ConfigureAwait(false);
            return;
        }

        var headline = Headline(classification, buildId);
        await pullRequests.CreateBranchAsync(branch, mainHead, cancellationToken).ConfigureAwait(false);
        var head = await pullRequests.CommitFileAsync(branch, mainHead, headline, LivemapMergeGate.BuildsPath, proposed, cancellationToken).ConfigureAwait(false);
        var number = await pullRequests.OpenAsync(branch, headline, Body(runId), cancellationToken).ConfigureAwait(false);
        if (!await builds.OpenPullRequestAsync(buildId, number, head, waiting: !options.Value.AutoMerge, cancellationToken).ConfigureAwait(false))
        {
            await pullRequests.ClosePullRequestAsync(number, cancellationToken).ConfigureAwait(false);
            await pullRequests.DeleteBranchAsync(branch, cancellationToken).ConfigureAwait(false);
        }
    }

    // True while the checks are still running; any other answer means the build's state moved and the plan is read again.
    [Activity(AdvanceName)]
    public async Task<bool> AdvanceAsync(long buildId)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var row = await builds.FindAsync(buildId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: LivemapBuildState.PullRequestOpen or LivemapBuildState.Merging })
        {
            return false;
        }

        if (row is not { PullRequestNumber: { } number, HeadSha: { } head, Classification: { } classification })
        {
            await builds.FlagPullRequestAsync(buildId, "The build has no pull request recorded.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (row.State == LivemapBuildState.PullRequestOpen && !options.Value.AutoMerge)
        {
            await builds.HoldAsync(buildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var pull = await pullRequests.GetAsync(number, cancellationToken).ConfigureAwait(false);
        if (pull is null || (pull.State != "open" && !pull.Merged))
        {
            await builds.FlagPullRequestAsync(buildId, pull is null ? "The pull request could not be found." : "The pull request was closed without being merged.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (pull.Merged)
        {
            await SettleMergedAsync(buildId, pull, head, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!options.Value.AutoMerge)
        {
            await builds.HoldAsync(buildId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var mainHead = await pullRequests.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var verdict = LivemapMergeGate.Evaluate(await ReadFactsAsync(buildId, pull, head, mainHead, cancellationToken).ConfigureAwait(false), options.Value.RequiredChecks);
        switch (verdict.Outcome)
        {
            case LivemapGateOutcome.Pending when time.GetUtcNow() - row.UpdatedAt <= ChecksLimit:
                return true;
            case LivemapGateOutcome.Pending:
                await builds.FlagPullRequestAsync(buildId, $"Required checks did not finish in time: {string.Join(", ", verdict.Names)}.", cancellationToken).ConfigureAwait(false);
                break;
            case LivemapGateOutcome.Failed:
                await builds.FlagPullRequestAsync(buildId, $"Required checks failed: {string.Join(", ", verdict.Names)}.", cancellationToken).ConfigureAwait(false);
                break;
            case LivemapGateOutcome.MainMoved:
                await builds.ReopenAsync(buildId, verdict.Reason!, LivemapBuildActivities.MaxAttempts, cancellationToken).ConfigureAwait(false);
                break;
            case LivemapGateOutcome.Passed:
                await MergeAsync(buildId, number, head, classification, mainHead, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await builds.FlagPullRequestAsync(buildId, verdict.Reason!, cancellationToken).ConfigureAwait(false);
                break;
        }

        return false;
    }

    private async Task<LivemapGateFacts> ReadFactsAsync(long buildId, LivemapPullRequest pull, string head, string mainHead, CancellationToken cancellationToken)
    {
        var branch = LivemapBuildBranch.For(buildId);
        var appLogin = await pullRequests.GetAppLoginAsync(cancellationToken).ConfigureAwait(false);
        var parents = await pullRequests.GetParentsAsync(pull.HeadSha, cancellationToken).ConfigureAwait(false);
        var files = await pullRequests.ListFilesAsync(pull.Number, cancellationToken).ConfigureAwait(false);
        var headMode = await pullRequests.GetFileModeAsync(pull.HeadSha, LivemapMergeGate.BuildsPath, cancellationToken).ConfigureAwait(false);
        var mainMode = await pullRequests.GetFileModeAsync(mainHead, LivemapMergeGate.BuildsPath, cancellationToken).ConfigureAwait(false);
        var checkRuns = await pullRequests.ListCheckRunsAsync(pull.HeadSha, cancellationToken).ConfigureAwait(false);
        return new LivemapGateFacts(
            Pull: pull,
            Fork: options.Value.Repo,
            Branch: branch,
            ExpectedHeadSha: head,
            AppLogin: appLogin,
            MainHead: mainHead,
            HeadParents: parents,
            Files: files,
            HeadFileMode: headMode,
            MainFileMode: mainMode,
            CheckRuns: checkRuns,
            CiSuiteIds: await CiSuitesAsync(checkRuns, branch, cancellationToken).ConfigureAwait(false));
    }

    // A required name only counts in the check suite of the fork's own CI run for the branch.
    private async Task<IReadOnlySet<long>> CiSuitesAsync(IReadOnlyList<LivemapCheckRun> checkRuns, string branch, CancellationToken cancellationToken)
    {
        var suites = new HashSet<long>();
        var candidates = checkRuns
            .Where(run => run.AppSlug == LivemapMergeGate.ChecksApp && run.CheckSuiteId > 0 && options.Value.RequiredChecks.Contains(run.Name))
            .Select(run => run.CheckSuiteId)
            .Distinct();
        foreach (var suite in candidates)
        {
            var runs = await github.ListSuiteRunsAsync(suite, cancellationToken).ConfigureAwait(false);
            if (runs.Count > 0 && runs.All(run => run.IsCiRunOf(branch)))
            {
                suites.Add(suite);
            }
        }

        return suites;
    }

    private async Task SettleMergedAsync(long buildId, LivemapPullRequest pull, string head, CancellationToken cancellationToken)
    {
        if (pull.HeadSha != head)
        {
            await builds.FlagPullRequestAsync(buildId, "The pull request was merged at another commit than the one the worker made.", cancellationToken).ConfigureAwait(false);
        }
        else if (await builds.BeginMergeAsync(buildId, cancellationToken).ConfigureAwait(false))
        {
            await builds.MergedAsync(buildId, cancellationToken).ConfigureAwait(false);
        }
    }

    // The merge is begun first so a retry after a lost answer goes on where it stopped; GitHub merges only the head given.
    private async Task MergeAsync(long buildId, int number, string head, LivemapClassification classification, string mainHead, CancellationToken cancellationToken)
    {
        if (!await builds.BeginMergeAsync(buildId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var title = $"{Headline(classification, buildId)} (#{number.ToString(CultureInfo.InvariantCulture)})";
        for (var retry = 0; ; retry++)
        {
            if (await ReopenIfMainMovedAsync(buildId, mainHead, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var result = await pullRequests.MergeAsync(number, head, title, cancellationToken).ConfigureAwait(false);
            if (result == LivemapMergeResult.Merged)
            {
                await builds.MergedAsync(buildId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var after = await pullRequests.GetAsync(number, cancellationToken).ConfigureAwait(false);
            if (after is { Merged: true } && after.HeadSha == head)
            {
                await builds.MergedAsync(buildId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result != LivemapMergeResult.NotMergeable || retry >= MergeRetries)
            {
                if (!await ReopenIfMainMovedAsync(buildId, mainHead, cancellationToken).ConfigureAwait(false))
                {
                    await builds.FlagPullRequestAsync(buildId, "GitHub refused to merge the pull request.", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await Task.Delay(MergeRetryWait, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReopenIfMainMovedAsync(long buildId, string mainHead, CancellationToken cancellationToken)
    {
        if (await pullRequests.GetMainHeadAsync(cancellationToken).ConfigureAwait(false) == mainHead)
        {
            return false;
        }

        await builds.ReopenAsync(buildId, MainMovedDetail, LivemapBuildActivities.MaxAttempts, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string Headline(LivemapClassification classification, long buildId) =>
        $"fix(builds): {(classification == LivemapClassification.Known ? "add" : "support")} server build {buildId.ToString(CultureInfo.InvariantCulture)}";

    private string Body(long compatRunId) =>
        $"Adds the build to `{LivemapMergeGate.BuildsPath}`.\n\nCompat run: https://github.com/{options.Value.Repo}/actions/runs/{compatRunId.ToString(CultureInfo.InvariantCulture)}\nTracking issue: #{options.Value.TrackingIssue.ToString(CultureInfo.InvariantCulture)}\n";

    private async Task StartOverAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await builds.RestartAsync(buildId, detail, LivemapBuildActivities.MaxAttempts, cancellationToken).ConfigureAwait(false);

    private async Task RemoveAsync(string branch, CancellationToken cancellationToken)
    {
        foreach (var pull in await pullRequests.FindOpenByBranchAsync(branch, cancellationToken).ConfigureAwait(false))
        {
            await pullRequests.ClosePullRequestAsync(pull.Number, cancellationToken).ConfigureAwait(false);
        }

        await pullRequests.DeleteBranchAsync(branch, cancellationToken).ConfigureAwait(false);
    }
}
