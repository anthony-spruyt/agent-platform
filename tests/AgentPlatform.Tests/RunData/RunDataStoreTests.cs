using System.Text.Json;
using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.RunData;

public class RunDataStoreTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (RunDataStore Store, IDbContextFactory<RunDataDbContext> Factory) NewStore()
    {
        var data = InMemoryRunData.Create();
        return (data.Store, data.Factory);
    }

    private static BeginRunInput Begin(string? promptBody = null, string? model = null) =>
        new("PingWorkflow", Json("""{"name":"x"}"""), Started, PromptName: promptBody is null ? null : "ping", PromptBody: promptBody, Model: model);

    private static RecordStepInput Step(Guid runId, int seq = 1, long? inputTokens = null, long? outputTokens = null, decimal? cost = null) =>
        new(runId, seq, "ping", LadderRung.Rule, "deterministic", Started, 5, InputTokens: inputTokens, OutputTokens: outputTokens, CostUsd: cost);

    [Fact]
    public async Task BeginRun_StoresTheInputEventAndStartsRunning()
    {
        var (store, factory) = NewStore();

        var result = await store.BeginRunAsync("wf-1", "run-1", Begin(), Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal("wf-1", run.WorkflowId);
        Assert.Equal("run-1", run.WorkflowRunId);
        Assert.Equal("PingWorkflow", run.WorkflowType);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(Started, run.StartedAt);
        Assert.Equal("x", Json(run.InputEvent).GetProperty("name").GetString());
        Assert.Null(run.CompletedAt);
        Assert.Null(run.Outcome);
    }

    [Fact]
    public async Task BeginRun_StoresTheFetchedContextAndModel()
    {
        var (store, factory) = NewStore();

        await store.BeginRunAsync("wf", "run", Begin(model: "sonnet") with { Context = Json("""{"changelog":"c"}""") }, Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        Assert.Equal("sonnet", run.Model);
        Assert.Equal("c", Json(run.Context!).GetProperty("changelog").GetString());
    }

    [Fact]
    public async Task BeginRun_StoresThePromptByHashAndLinksTheRun()
    {
        var (store, factory) = NewStore();

        var result = await store.BeginRunAsync("wf", "run", Begin("Say hello"), Ct);

        Assert.Equal(PromptHashing.Compute("Say hello"), result.PromptHash);
        await using var db = await factory.CreateDbContextAsync(Ct);
        var prompt = await db.Prompts.SingleAsync(Ct);
        Assert.Equal(result.PromptHash, prompt.Hash);
        Assert.Equal("ping", prompt.Name);
        Assert.Equal("Say hello", prompt.Body);
        Assert.Equal(result.PromptHash, (await db.Runs.SingleAsync(Ct)).PromptHash);
    }

    [Fact]
    public async Task BeginRun_SharesOnePromptRowBetweenRunsWithTheSameBody()
    {
        var (store, factory) = NewStore();

        var first = await store.BeginRunAsync("wf-1", "run", Begin("same"), Ct);
        var second = await store.BeginRunAsync("wf-2", "run", Begin("same"), Ct);

        Assert.Equal(first.PromptHash, second.PromptHash);
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.Prompts.CountAsync(Ct));
        Assert.Equal(2, await db.Runs.CountAsync(Ct));
    }

    [Fact]
    public async Task BeginRun_RetriedForTheSameWorkflowRun_ReturnsTheExistingRun()
    {
        var (store, factory) = NewStore();

        var first = await store.BeginRunAsync("wf", "run", Begin("p"), Ct);
        var retry = await store.BeginRunAsync("wf", "run", Begin("p"), Ct);

        Assert.Equal(first, retry);
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.Runs.CountAsync(Ct));
    }

    [Fact]
    public async Task BeginRun_NewRunOfTheSameWorkflowId_IsADifferentRow()
    {
        var (store, _) = NewStore();

        var first = await store.BeginRunAsync("wf", "run-1", Begin(), Ct);
        var second = await store.BeginRunAsync("wf", "run-2", Begin(), Ct);

        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task BeginRun_WithoutAnInputEvent_IsRejected()
    {
        var (store, _) = NewStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.BeginRunAsync("wf", "run", Begin() with { InputEvent = default }, Ct));
    }

    [Fact]
    public async Task RecordStep_StoresTheLadderStepDetails()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin("p"), Ct);

        await store.RecordStepAsync(
            new RecordStepInput(
                run.RunId, 1, "triage", LadderRung.Jev, "confident", Started, 1200,
                Input: Json("""{"q":1}"""), Output: Json("""{"verdict":"safe"}"""), Confidence: 0.93,
                PromptHash: run.PromptHash, Model: "jev", InputTokens: 10, OutputTokens: 20, CostUsd: 0.0125m),
            Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var step = await db.RunSteps.SingleAsync(Ct);
        Assert.Equal(run.RunId, step.RunId);
        Assert.Equal(1, step.Seq);
        Assert.Equal("triage", step.Name);
        Assert.Equal(LadderRung.Jev, step.Rung);
        Assert.Equal("confident", step.StopReason);
        Assert.Equal(0.93, step.Confidence);
        Assert.Equal(1200, step.DurationMs);
        Assert.Equal(run.PromptHash, step.PromptHash);
        Assert.Equal("jev", step.Model);
        Assert.Equal(10, step.InputTokens);
        Assert.Equal(20, step.OutputTokens);
        Assert.Equal(0.0125m, step.CostUsd);
        Assert.Equal(1, Json(step.Input!).GetProperty("q").GetInt32());
        Assert.Equal("safe", Json(step.Output!).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task RecordStep_RetriedForTheSameSequence_StoresOneRow()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);

        await store.RecordStepAsync(Step(run.RunId), Ct);
        await store.RecordStepAsync(Step(run.RunId), Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await db.RunSteps.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordStep_ForAnUnknownRun_Throws()
    {
        var (store, _) = NewStore();

        await Assert.ThrowsAsync<UnknownRunException>(() => store.RecordStepAsync(Step(Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task BeginRun_StripsNulCharactersPostgresRejects()
    {
        var (store, factory) = NewStore();
        var input = new BeginRunInput(
            "Ping\u0000Workflow",
            Json("""{"na\u0000me":"a\u0000b","literal":"x\\u0000y"}"""),
            Started,
            PromptName: "pi\u0000ng",
            PromptBody: "bo\u0000dy",
            Model: "mo\u0000del") with
        { Context = Json("""["c\u0000\u0000d"]""") };

        await store.BeginRunAsync("wf\u0000", "run\u0000", input, Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var run = await db.Runs.SingleAsync(Ct);
        var prompt = await db.Prompts.SingleAsync(Ct);
        Assert.Equal("wf", run.WorkflowId);
        Assert.Equal("run", run.WorkflowRunId);
        Assert.Equal("PingWorkflow", run.WorkflowType);
        Assert.Equal("model", run.Model);
        Assert.Equal("ping", prompt.Name);
        Assert.Equal("body", prompt.Body);
        Assert.Equal(PromptHashing.Compute("body"), prompt.Hash);
        var inputEvent = Json(run.InputEvent);
        Assert.Equal("ab", inputEvent.GetProperty("name").GetString());
        Assert.Equal("x\\u0000y", inputEvent.GetProperty("literal").GetString());
        Assert.Equal("cd", Json(run.Context!)[0].GetString());
    }

    [Fact]
    public async Task RecordStep_StripsNulCharactersPostgresRejects()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);

        await store.RecordStepAsync(
            Step(run.RunId) with
            {
                Name = "pi\u0000ng",
                StopReason = "deter\u0000ministic",
                Model = "mo\u0000del",
                Input = Json("""{"k":"a\u0000b"}"""),
                Output = Json("""{"k":"c\u0000d"}"""),
            },
            Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var step = await db.RunSteps.SingleAsync(Ct);
        Assert.Equal("ping", step.Name);
        Assert.Equal("deterministic", step.StopReason);
        Assert.Equal("model", step.Model);
        Assert.Equal("ab", Json(step.Input!).GetProperty("k").GetString());
        Assert.Equal("cd", Json(step.Output!).GetProperty("k").GetString());
    }

    [Fact]
    public async Task RecordStep_WithAnUnknownPromptHash_IsRejectedBeforeAnyWrite()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordStepAsync(Step(run.RunId) with { PromptHash = new string('0', 64) }, Ct));

        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(0, await db.RunSteps.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordStep_WithAKnownPromptHash_IsStored()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin("p"), Ct);

        await store.RecordStepAsync(Step(run.RunId) with { PromptHash = run.PromptHash }, Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(run.PromptHash, (await db.RunSteps.SingleAsync(Ct)).PromptHash);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task RecordStep_ConfidenceOutsideZeroToOne_IsRejected(double confidence)
    {
        var (store, _) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.RecordStepAsync(Step(run.RunId) with { Confidence = confidence }, Ct));
    }

    [Fact]
    public async Task CompleteRun_SetsStatusDurationAndSumsTheStepUsage()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);
        await store.RecordStepAsync(Step(run.RunId, 1, 10, 20, 0.5m), Ct);
        await store.RecordStepAsync(Step(run.RunId, 2, 1, 2, 0.25m), Ct);
        await store.RecordStepAsync(Step(run.RunId, 3), Ct);

        await store.CompleteRunAsync(new CompleteRunInput(run.RunId, RunStatus.Succeeded, Started.AddSeconds(3), "sonnet"), Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var row = await db.Runs.SingleAsync(Ct);
        Assert.Equal(RunStatus.Succeeded, row.Status);
        Assert.Equal(Started.AddSeconds(3), row.CompletedAt);
        Assert.Equal(3000, row.DurationMs);
        Assert.Equal(11, row.InputTokens);
        Assert.Equal(22, row.OutputTokens);
        Assert.Equal(0.75m, row.CostUsd);
        Assert.Equal("sonnet", row.Model);
    }

    [Fact]
    public async Task CompleteRun_WithoutStepUsage_LeavesTheTotalsNull()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(model: "kept"), Ct);
        await store.RecordStepAsync(Step(run.RunId), Ct);

        await store.CompleteRunAsync(new CompleteRunInput(run.RunId, RunStatus.Failed, Started.AddSeconds(1)), Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var row = await db.Runs.SingleAsync(Ct);
        Assert.Equal(RunStatus.Failed, row.Status);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CostUsd);
        Assert.Equal("kept", row.Model);
    }

    [Fact]
    public async Task CompleteRun_Retried_KeepsTheSameResult()
    {
        var (store, factory) = NewStore();
        var run = await store.BeginRunAsync("wf", "run", Begin(), Ct);
        await store.RecordStepAsync(Step(run.RunId, 1, 10, 20, 0.5m), Ct);
        var input = new CompleteRunInput(run.RunId, RunStatus.Succeeded, Started.AddSeconds(2));

        await store.CompleteRunAsync(input, Ct);
        await store.CompleteRunAsync(input, Ct);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var row = await db.Runs.SingleAsync(Ct);
        Assert.Equal(10, row.InputTokens);
        Assert.Equal(2000, row.DurationMs);
    }

    [Fact]
    public async Task CompleteRun_ForAnUnknownRun_Throws()
    {
        var (store, _) = NewStore();

        await Assert.ThrowsAsync<UnknownRunException>(
            () => store.CompleteRunAsync(new CompleteRunInput(Guid.NewGuid(), RunStatus.Succeeded, Started), Ct));
    }
}
