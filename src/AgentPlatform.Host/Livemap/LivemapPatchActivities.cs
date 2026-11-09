using System.Globalization;
using AgentPlatform.Host.Agents;
using Microsoft.Extensions.Options;
using Temporalio.Activities;

namespace AgentPlatform.Host.Livemap;

public enum LivemapPatchStartKind
{
    Ready,
    Stop,
}

public enum LivemapPatchVerdict
{
    Stored,
    Retry,
    NeedsPerson,
    Superseded,
}

public sealed record LivemapPatchPrepareInput(long BuildId, string Template);

public sealed record LivemapPatchBeginInput(long BuildId, int Run);

public sealed record LivemapPatchAcceptInput(long BuildId, int Run, string MainSha, LivemapResult? Result, string? Failure);

public sealed record LivemapPatchParkInput(long BuildId, string Detail, DateTimeOffset Until, bool RefundRun);

// A Stop carries only the outcome the workflow ends with; a Ready start carries everything the pod step and the budget need.
public sealed record LivemapPatchStart(
    LivemapPatchStartKind Kind,
    LivemapBuildOutcome Outcome,
    int Run,
    string Prompt,
    string MainSha,
    string Fork,
    string Model,
    int DeadlineSeconds,
    AgentRunOverrides? Overrides)
{
    public static LivemapPatchStart Stopped(LivemapBuildOutcome outcome) =>
        new(LivemapPatchStartKind.Stop, outcome, 0, string.Empty, string.Empty, string.Empty, string.Empty, 0, null);
}

public sealed class LivemapPatchActivities(
    IOptions<LivemapOptions> options,
    LivemapBuildStore builds,
    ILivemapPullRequests pullRequests,
    ILivemapGitHub github,
    LivemapPatchValidator validator)
{
    public const string PrepareName = "LivemapPatchPrepare";
    public const string BeginName = "LivemapPatchBegin";
    public const string AcceptName = "LivemapPatchAccept";
    public const string ParkName = "LivemapPatchPark";
    public const string NeedsPersonName = "LivemapPatchNeedsPerson";

    public const string ExeImageRepository = "ghcr.io/anthony-spruyt/enshrouded-server-exe";

    private const int MaxExcerptChars = 600;

    private sealed record Baseline(LivemapPatchBase Base, string ReportJson);

    [Activity(PrepareName)]
    public async Task<LivemapPatchStart> PrepareAsync(LivemapPatchPrepareInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var config = options.Value;
        var row = await builds.FindAsync(input.BuildId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: LivemapBuildState.Patching })
        {
            return LivemapPatchStart.Stopped(LivemapBuildOutcome.Superseded);
        }

        if (row.AgentRuns >= config.AgentRunsPerBuild)
        {
            var detail = string.Create(CultureInfo.InvariantCulture, $"The agent made {row.AgentRuns} runs without a patch; the build needs a person.");
            return await StopForPersonAsync(row.BuildId, detail, cancellationToken).ConfigureAwait(false);
        }

        var mainSha = await pullRequests.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var (baseline, problem) = await ReadBaselineAsync(row, mainSha, cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            return await StopForPersonAsync(row.BuildId, problem!, cancellationToken).ConfigureAwait(false);
        }

        var (previousId, previousRow, previousProblem) = FindPreviousRow(baseline);
        if (previousRow is null)
        {
            return await StopForPersonAsync(row.BuildId, previousProblem!, cancellationToken).ConfigureAwait(false);
        }

        var run = row.AgentRuns + 1;
        var inputs = new LivemapHardPatchInputs(row.BuildId, row.ManifestGid, previousId, previousRow, baseline.ReportJson, mainSha, row.PatchNotes, run, config.AgentRunsPerBuild);
        var overrides = new AgentRunOverrides(
            Model: config.AgentModel,
            MaxTurns: config.AgentMaxTurns,
            MaxBudgetUsd: config.AgentMaxBudgetUsd,
            TokenPermissions: new Dictionary<string, string> { ["contents"] = "read" },
            ImageVolumes: [new AgentImageVolume(LivemapHardPatchPrompt.PreviousExeVolume, string.Create(CultureInfo.InvariantCulture, $"{ExeImageRepository}:{previousId}"))]);
        return new LivemapPatchStart(
            LivemapPatchStartKind.Ready,
            LivemapBuildOutcome.Classified,
            run,
            LivemapHardPatchPrompt.Render(input.Template, config, inputs),
            mainSha,
            config.Repo,
            config.AgentModel,
            config.AgentDeadlineSeconds,
            overrides);
    }

    [Activity(BeginName)]
    public async Task<bool> BeginAsync(LivemapPatchBeginInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return await builds.BeginAgentRunAsync(input.BuildId, input.Run, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);
    }

    [Activity(AcceptName)]
    public async Task<LivemapPatchVerdict> AcceptAsync(LivemapPatchAcceptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var row = await builds.FindAsync(input.BuildId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: LivemapBuildState.Patching })
        {
            return LivemapPatchVerdict.Superseded;
        }

        var result = input.Result;
        if (result is null)
        {
            return await RetryAsync(row, input.Run, input.Failure ?? "the run left no result", cancellationToken).ConfigureAwait(false);
        }

        switch (result.Status)
        {
            case LivemapResultStatus.GaveUp:
                return await SendToPersonAsync(row.BuildId, GaveUpDetail(result.Summary), cancellationToken).ConfigureAwait(false);
            case LivemapResultStatus.Partial:
                return await RetryAsync(row, input.Run, $"partial result: {result.Notes}", cancellationToken).ConfigureAwait(false);
        }

        var (baseline, problem) = await ReadBaselineAsync(row, input.MainSha, cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            return await SendToPersonAsync(row.BuildId, problem!, cancellationToken).ConfigureAwait(false);
        }

        if (validator.Check(result, baseline.Base, row.BuildId, row.ManifestGid) is { } refused)
        {
            return await RetryAsync(row, input.Run, $"the result was refused: {refused}", cancellationToken).ConfigureAwait(false);
        }

        return await builds.StorePatchAsync(row.BuildId, LivemapStoredPatch.Write(result), input.MainSha, cancellationToken).ConfigureAwait(false)
            ? LivemapPatchVerdict.Stored
            : LivemapPatchVerdict.Superseded;
    }

    [Activity(ParkName)]
    public async Task<LivemapBuildOutcome> ParkAsync(LivemapPatchParkInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return await builds.ParkUntilAsync(input.BuildId, input.Detail, input.Until, input.RefundRun, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false)
            ? LivemapBuildOutcome.Parked
            : LivemapBuildOutcome.Superseded;
    }

    [Activity(NeedsPersonName)]
    public async Task<LivemapBuildOutcome> NeedsPersonAsync(LivemapParkInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return await builds.NeedsPersonAsync(input.BuildId, input.Detail, ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false)
            ? LivemapBuildOutcome.NeedsOwner
            : LivemapBuildOutcome.Superseded;
    }

    // The agent's words are shown as a code block with its backticks swapped out, so they cannot link, mention or close the block.
    private static string GaveUpDetail(string summary)
    {
        var excerpt = LivemapBuildStore.CutOnCharacter(summary.Replace('`', '\''), MaxExcerptChars);
        return $"The agent gave up on the build. Its summary:\n\n```\n{excerpt}\n```";
    }

    private static (long Id, string? Row, string? Problem) FindPreviousRow(Baseline baseline)
    {
        var label = baseline.Base.Report.BaseLabel;
        if (!long.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
        {
            return (0, null, "The base build of the compat report is not a build id.");
        }

        var rows = CompatReport.ParseObject(baseline.Base.BuildsJson) is { } root ? CompatClassifier.Rows(root) : null;
        var previous = rows?.Select(row => row!.AsObject()).FirstOrDefault(row => CompatClassifier.Label(row) == label);
        return previous is null
            ? (0, null, $"The last supported build {label} is not a row of the fork's builds file on main.")
            : (id, previous.ToJsonString(), null);
    }

    private async Task<(Baseline? Baseline, string? Problem)> ReadBaselineAsync(LivemapBuild row, string sha, CancellationToken cancellationToken)
    {
        if (row.CompatRunId is not { } runId)
        {
            return (null, "The build has no compat run to patch from.");
        }

        CompatArtifact? artifact;
        try
        {
            artifact = await github.ReadCompatArtifactAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (CompatArtifactException ex)
        {
            return (null, ex.Message);
        }

        if (artifact is null)
        {
            return (null, "The compat run's artifact is no longer there.");
        }

        if (!CompatReport.TryParse(artifact.ReportJson, row.BuildId, row.ManifestGid, out var report, out var problem))
        {
            return (null, problem);
        }

        var current = await github.ReadBuildsJsonAsync(sha, cancellationToken).ConfigureAwait(false);
        var schemaJson = await github.ReadBuildsSchemaAsync(sha, cancellationToken).ConfigureAwait(false);
        return current is null || schemaJson is null
            ? (null, "The fork has no builds file or no builds schema on main.")
            : (new Baseline(new LivemapPatchBase(current, schemaJson, report!), artifact.ReportJson), null);
    }

    // Newest first, so the cap on the notes cuts the oldest.
    private async Task<LivemapPatchVerdict> RetryAsync(LivemapBuild row, int run, string note, CancellationToken cancellationToken)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"Run {run}: {note}");
        var notes = string.IsNullOrEmpty(row.PatchNotes) ? line : $"{line}\n\n{row.PatchNotes}";
        return await builds.SavePartialAsync(row.BuildId, notes, cancellationToken).ConfigureAwait(false)
            ? LivemapPatchVerdict.Retry
            : LivemapPatchVerdict.Superseded;
    }

    private async Task<LivemapPatchVerdict> SendToPersonAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        await builds.NeedsPersonAsync(buildId, detail, cancellationToken).ConfigureAwait(false)
            ? LivemapPatchVerdict.NeedsPerson
            : LivemapPatchVerdict.Superseded;

    private async Task<LivemapPatchStart> StopForPersonAsync(long buildId, string detail, CancellationToken cancellationToken) =>
        LivemapPatchStart.Stopped(await builds.NeedsPersonAsync(buildId, detail, cancellationToken).ConfigureAwait(false)
            ? LivemapBuildOutcome.NeedsOwner
            : LivemapBuildOutcome.Superseded);
}
