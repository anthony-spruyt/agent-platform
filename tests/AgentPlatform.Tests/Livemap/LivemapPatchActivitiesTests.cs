using System.Text.RegularExpressions;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Livemap;
using Temporalio.Testing;

using static AgentPlatform.Tests.Livemap.LivemapBuildRig;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPatchActivitiesTests
{
    private static readonly string _template = AgentPrompts.Load(AgentPrompts.LivemapHardPatch);

    private static LivemapResult DataPatch() =>
        new(LivemapResultStatus.Data, "Added the row.", EasyArtifact().BuildsJson, [], null);

    private static LivemapResult CodePatch() =>
        new(LivemapResultStatus.Code, "Moved the hook.", EasyArtifact().BuildsJson, [new LivemapFile("src/movement.cpp", "// hook\n")], null);

    private static Task<LivemapPatchStart> PrepareAsync(LivemapBuildRig rig) =>
        new ActivityEnvironment().RunAsync(() => rig.PatchActivities().PrepareAsync(new LivemapPatchPrepareInput(BuildId, _template)));

    private static Task<LivemapPatchVerdict> AcceptAsync(LivemapBuildRig rig, LivemapResult? result, string? failure = null, int run = 1, string? mainSha = null) =>
        new ActivityEnvironment().RunAsync(() => rig.PatchActivities().AcceptAsync(new LivemapPatchAcceptInput(BuildId, run, mainSha ?? FakeLivemapPullRequests.StartMain, result, failure)));

    private static async Task<LivemapBuildRig> PatchingRigAsync()
    {
        var rig = new LivemapBuildRig();
        rig.Config.HardPath = true;
        await rig.SeedPatchingAsync();
        return rig;
    }

    [Fact]
    public async Task Prepare_ReadsMainsHead_AndTheFilesAtThatCommit_ForTheRun()
    {
        var rig = await PatchingRigAsync();
        rig.PullRequests.MainHead = new string('d', 40);

        var start = await PrepareAsync(rig);

        Assert.Equal(LivemapPatchStartKind.Ready, start.Kind);
        Assert.Equal(new string('d', 40), start.MainSha);
        Assert.Equal(new string('d', 40), rig.GitHub.BuildsReadAt);
        Assert.Equal(new string('d', 40), rig.GitHub.SchemaReadAt);
        Assert.Contains(new string('d', 40), start.Prompt, StringComparison.Ordinal);
        Assert.Equal((FakeLivemapPullRequests.Fork, 1), (start.Fork, start.Run));
    }

    [Fact]
    public async Task Prepare_TakesTheModelAndTheLimitsFromTheConfiguration()
    {
        var rig = await PatchingRigAsync();
        rig.Config.AgentModel = "sonnet";
        rig.Config.AgentMaxTurns = 77;
        rig.Config.AgentMaxBudgetUsd = 3.5m;
        rig.Config.AgentDeadlineSeconds = 1234;

        var start = await PrepareAsync(rig);

        Assert.Equal(("sonnet", 1234), (start.Model, start.DeadlineSeconds));
        Assert.Equal(("sonnet", 77, 3.5m), (start.Overrides!.Model, start.Overrides.MaxTurns, start.Overrides.MaxBudgetUsd));
    }

    [Fact]
    public async Task Prepare_GivesThePodOnlyReadAccessToTheFork()
    {
        var rig = await PatchingRigAsync();

        var start = await PrepareAsync(rig);

        var permissions = Assert.Single(start.Overrides!.TokenPermissions!);
        Assert.Equal(("contents", "read"), (permissions.Key, permissions.Value));
        start.Overrides.Validate();
    }

    [Fact]
    public async Task Prepare_MountsTheLastSupportedExe_AsAnImageVolume_NamedInThePrompt()
    {
        var rig = await PatchingRigAsync();

        var start = await PrepareAsync(rig);

        var volume = Assert.Single(start.Overrides!.ImageVolumes!);
        Assert.Equal(("exe", "ghcr.io/anthony-spruyt/enshrouded-server-exe:300"), (volume.Name, volume.Reference));
        Assert.Contains($"{AgentPodSpecBuilder.ImageVolumePath(volume.Name)}/enshrouded_server.exe", start.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_PutsTheReport_ThePreviousRow_AndTheEarlierNotes_InThePrompt()
    {
        var rig = await PatchingRigAsync();
        await rig.Store.SavePartialAsync(BuildId, "Run 1: the movement site moved by 0x40.", Ct);

        var start = await PrepareAsync(rig);

        Assert.Contains("site movement matched 0 times", start.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"mining_layout\":\"stack_record\"", start.Prompt, StringComparison.Ordinal);
        Assert.Contains("Run 1: the movement site moved by 0x40.", start.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_NumbersTheRunAfterTheOnesAlreadyCounted()
    {
        var rig = await PatchingRigAsync();
        Assert.True(await rig.Store.BeginAgentRunAsync(BuildId, 1, Ct));

        var start = await PrepareAsync(rig);

        Assert.Equal(2, start.Run);
        Assert.Contains("run 2 of 3", start.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_AtTheRunCap_SendsTheBuildToAPerson_WithoutReadingTheFork()
    {
        var rig = await PatchingRigAsync();
        rig.Config.AgentRunsPerBuild = 2;
        Assert.True(await rig.Store.BeginAgentRunAsync(BuildId, 1, Ct));
        Assert.True(await rig.Store.BeginAgentRunAsync(BuildId, 2, Ct));

        var start = await PrepareAsync(rig);

        Assert.Equal((LivemapPatchStartKind.Stop, LivemapBuildOutcome.NeedsOwner), (start.Kind, start.Outcome));
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("needs a person", row.Detail, StringComparison.Ordinal);
        Assert.Contains("2 runs", row.Detail, StringComparison.Ordinal);
        Assert.Null(rig.GitHub.BuildsReadAt);
    }

    [Fact]
    public async Task Prepare_WithTheHardPathOff_SendsThePatchingBuildToAPerson_WithoutReadingTheFork()
    {
        var rig = await PatchingRigAsync();
        rig.Config.HardPath = false;

        var start = await PrepareAsync(rig);

        Assert.Equal((LivemapPatchStartKind.Stop, LivemapBuildOutcome.NeedsOwner), (start.Kind, start.Outcome));
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, 0), (row.State, row.AgentRuns));
        Assert.Contains("hard path is off", row.Detail, StringComparison.Ordinal);
        Assert.Null(rig.GitHub.BuildsReadAt);
    }

    [Fact]
    public async Task Prepare_ForABuildThatIsNotBeingPatched_Stops_AndTouchesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedClassifiedAsync();

        var start = await PrepareAsync(rig);

        Assert.Equal((LivemapPatchStartKind.Stop, LivemapBuildOutcome.Superseded), (start.Kind, start.Outcome));
        Assert.Equal(LivemapBuildState.Classified, (await rig.RowAsync()).State);
        Assert.Null(rig.GitHub.BuildsReadAt);
    }

    [Fact]
    public async Task Prepare_WhenTheCompatArtifactIsGone_SendsTheBuildToAPerson()
    {
        var rig = await PatchingRigAsync();
        rig.GitHub.Artifacts.Clear();

        var start = await PrepareAsync(rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, start.Outcome);
        Assert.Contains("artifact", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_WhenTheReportCannotBeRead_SendsTheBuildToAPerson()
    {
        var rig = await PatchingRigAsync();
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = new CompatArtifact("""{ "result": "hard" }""", null);

        var start = await PrepareAsync(rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, start.Outcome);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task Prepare_WhenTheLastSupportedBuildIsNotInMain_SendsTheBuildToAPerson()
    {
        var rig = await PatchingRigAsync();
        rig.GitHub.ForkBuilds = ForkBuilds.Replace("\"label\": \"300\"", "\"label\": \"250\"", StringComparison.Ordinal);

        var start = await PrepareAsync(rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, start.Outcome);
        Assert.Contains("300", (await rig.RowAsync()).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x:latest")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Prepare_WhenTheBaseOfTheReportIsNotABuildId_NeverMakesItAnImageTag(string label)
    {
        var rig = await PatchingRigAsync();
        var report = HardArtifact().ReportJson.Replace("\"label\":\"300\"", $"\"label\":\"{label}\"", StringComparison.Ordinal);
        rig.GitHub.Artifacts[FakeLivemapGitHub.CompatRunId] = new CompatArtifact(report, null);
        rig.GitHub.ForkBuilds = ForkBuilds.Replace("\"label\": \"300\"", $"\"label\": \"{label}\"", StringComparison.Ordinal);

        var start = await PrepareAsync(rig);

        Assert.Equal((LivemapPatchStartKind.Stop, LivemapBuildOutcome.NeedsOwner), (start.Kind, start.Outcome));
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task Prepare_WhenTheFilesAtMainAreMissing_SendsTheBuildToAPerson()
    {
        var rig = await PatchingRigAsync();
        rig.GitHub.ForkSchema = null;

        var start = await PrepareAsync(rig);

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, start.Outcome);
    }

    [Fact]
    public async Task Begin_CountsTheRun_AndARepeatOfTheSameRunIsHarmless()
    {
        var rig = await PatchingRigAsync();
        var activities = rig.PatchActivities();

        Assert.True(await new ActivityEnvironment().RunAsync(() => activities.BeginAsync(new LivemapPatchBeginInput(BuildId, 1))));
        Assert.True(await new ActivityEnvironment().RunAsync(() => activities.BeginAsync(new LivemapPatchBeginInput(BuildId, 1))));
        Assert.False(await new ActivityEnvironment().RunAsync(() => activities.BeginAsync(new LivemapPatchBeginInput(BuildId, 3))));

        Assert.Equal(1, (await rig.RowAsync()).AgentRuns);
    }

    [Fact]
    public async Task Accept_StoresADataPatch_AtTheShaTheRunSaw_AndHandsTheBuildOn()
    {
        var rig = await PatchingRigAsync();
        await rig.Store.SavePartialAsync(BuildId, "Run 1: earlier notes.", Ct);
        var sha = new string('c', 40);

        var verdict = await AcceptAsync(rig, DataPatch(), mainSha: sha);

        Assert.Equal(LivemapPatchVerdict.Stored, verdict);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Classified, LivemapClassification.Hard, sha), (row.State, row.Classification, row.PatchBaseSha));
        Assert.Null(row.PatchNotes);
        var stored = LivemapStoredPatch.Read(row.PatchResult);
        Assert.Equal((LivemapResultStatus.Data, DataPatch().BuildsJson), (stored!.Status, stored.BuildsJson));
        Assert.Equal(sha, rig.GitHub.BuildsReadAt);
        Assert.Equal(sha, rig.GitHub.SchemaReadAt);
    }

    [Fact]
    public async Task Accept_StoresACodePatch_WithItsFiles()
    {
        var rig = await PatchingRigAsync();

        var verdict = await AcceptAsync(rig, CodePatch());

        Assert.Equal(LivemapPatchVerdict.Stored, verdict);
        var stored = LivemapStoredPatch.Read((await rig.RowAsync()).PatchResult);
        Assert.Equal(LivemapResultStatus.Code, stored!.Status);
        Assert.Equal(CodePatch().Files, stored.Files);
    }

    [Fact]
    public async Task Accept_ChecksThePatch_AgainstTheReportAndTheFilesAtMain()
    {
        var rig = await PatchingRigAsync();
        var otherRow = EasyArtifact().BuildsJson!.Replace("0x3000", "0x4000", StringComparison.Ordinal);

        var verdict = await AcceptAsync(rig, DataPatch() with { BuildsJson = otherRow });

        Assert.Equal(LivemapPatchVerdict.Retry, verdict);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Patching, row.State);
        Assert.Null(row.PatchResult);
        Assert.Contains("fingerprint", row.PatchNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_APatchThatTheSchemaRefuses_IsNotStored_AndTheNotesSayWhy()
    {
        var rig = await PatchingRigAsync();
        rig.BuildsSchema.Problem = "builds[1].sigset is required";

        var verdict = await AcceptAsync(rig, DataPatch());

        Assert.Equal(LivemapPatchVerdict.Retry, verdict);
        var row = await rig.RowAsync();
        Assert.Null(row.PatchResult);
        Assert.Contains("builds[1].sigset is required", row.PatchNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_APartialResult_SavesItsNotesForTheNextRun_NewestFirst()
    {
        var rig = await PatchingRigAsync();
        await rig.Store.SavePartialAsync(BuildId, "Run 1: the first finding.", Ct);

        var verdict = await AcceptAsync(rig, new LivemapResult(LivemapResultStatus.Partial, "Stuck.", null, [], "the second finding"), run: 2);

        Assert.Equal(LivemapPatchVerdict.Retry, verdict);
        var notes = (await rig.RowAsync()).PatchNotes!;
        Assert.True(notes.IndexOf("Run 2", StringComparison.Ordinal) < notes.IndexOf("Run 1", StringComparison.Ordinal));
        Assert.Contains("the second finding", notes, StringComparison.Ordinal);
        Assert.Contains("the first finding", notes, StringComparison.Ordinal);
        Assert.Equal(LivemapBuildState.Patching, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task Accept_ARunThatLeftNoResult_IsNoted_AndTheBuildTriesAgain()
    {
        var rig = await PatchingRigAsync();

        var verdict = await AcceptAsync(rig, null, "the pod ran out of time");

        Assert.Equal(LivemapPatchVerdict.Retry, verdict);
        var notes = (await rig.RowAsync()).PatchNotes;
        Assert.Contains("Run 1", notes, StringComparison.Ordinal);
        Assert.Contains("the pod ran out of time", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_GavingUp_SendsTheBuildToAPerson_WithTheSummaryAsPlainText()
    {
        var rig = await PatchingRigAsync();
        var summary = "The hook is gone. See ```the end``` and ping @someone.\nAlso https://example.test/x";

        var verdict = await AcceptAsync(rig, new LivemapResult(LivemapResultStatus.GaveUp, summary, null, [], null));

        Assert.Equal(LivemapPatchVerdict.NeedsPerson, verdict);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.NeedsOwner, row.State);
        Assert.Contains("@someone", row.Detail, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(row.Detail!, "```"));
        Assert.EndsWith("```", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_GavingUp_KeepsTheDetailWithinItsLimit_AndTheFenceClosed()
    {
        var rig = await PatchingRigAsync();

        await AcceptAsync(rig, new LivemapResult(LivemapResultStatus.GaveUp, new string('x', LivemapResult.MaxSummaryLength), null, [], null));

        var detail = (await rig.RowAsync()).Detail!;
        Assert.True(detail.Length <= LivemapBuildStore.MaxDetailChars);
        Assert.EndsWith("```", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_ForABuildThatMovedOn_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedClassifiedAsync();

        var verdict = await AcceptAsync(rig, DataPatch());

        Assert.Equal(LivemapPatchVerdict.Superseded, verdict);
        var row = await rig.RowAsync();
        Assert.Equal(LivemapBuildState.Classified, row.State);
        Assert.Null(row.PatchResult);
        Assert.Null(rig.GitHub.BuildsReadAt);
    }

    [Fact]
    public async Task Accept_WhenTheCompatArtifactIsGone_SendsTheBuildToAPerson()
    {
        var rig = await PatchingRigAsync();
        rig.GitHub.Artifacts.Clear();

        var verdict = await AcceptAsync(rig, DataPatch());

        Assert.Equal(LivemapPatchVerdict.NeedsPerson, verdict);
        Assert.Equal(LivemapBuildState.NeedsOwner, (await rig.RowAsync()).State);
    }

    [Fact]
    public async Task Park_KeepsTheBuildUntilTheTime_AndGivesBackTheRunOnlyWhenAsked()
    {
        var rig = await PatchingRigAsync();
        Assert.True(await rig.Store.BeginAgentRunAsync(BuildId, 1, Ct));
        var until = Now.AddHours(5);

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.PatchActivities().ParkAsync(new LivemapPatchParkInput(BuildId, "The usage limit was hit.", until, RefundRun: true)));

        Assert.Equal(LivemapBuildOutcome.Parked, outcome);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.Parked, until, 0), (row.State, row.NextRetryAt, row.AgentRuns));
    }

    [Fact]
    public async Task Park_WithoutARefund_KeepsTheRunCounted()
    {
        var rig = await PatchingRigAsync();
        Assert.True(await rig.Store.BeginAgentRunAsync(BuildId, 1, Ct));

        await new ActivityEnvironment().RunAsync(() => rig.PatchActivities().ParkAsync(new LivemapPatchParkInput(BuildId, "Waiting.", Now.AddHours(1), RefundRun: false)));

        Assert.Equal(1, (await rig.RowAsync()).AgentRuns);
    }

    [Fact]
    public async Task NeedsPerson_StopsTheBuild_WithTheDetail()
    {
        var rig = await PatchingRigAsync();

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.PatchActivities().NeedsPersonAsync(new LivemapParkInput(BuildId, "The budget is spent.")));

        Assert.Equal(LivemapBuildOutcome.NeedsOwner, outcome);
        var row = await rig.RowAsync();
        Assert.Equal((LivemapBuildState.NeedsOwner, "The budget is spent."), (row.State, row.Detail));
    }

    [Fact]
    public async Task NeedsPerson_ForABuildThatMovedOn_ChangesNothing()
    {
        var rig = new LivemapBuildRig();
        await rig.SeedAsync();

        var outcome = await new ActivityEnvironment().RunAsync(() => rig.PatchActivities().NeedsPersonAsync(new LivemapParkInput(BuildId, "Late.")));

        Assert.Equal(LivemapBuildOutcome.Superseded, outcome);
        Assert.Equal(LivemapBuildState.Detected, (await rig.RowAsync()).State);
    }
}
