using AgentPlatform.Host.RunData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AgentPlatform.Tests.RunData;

public class RunDataModelTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost").Options;
        using var db = new RunDataDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static List<string> Columns(Type entity)
    {
        var type = Model().FindEntityType(entity)!;
        var table = StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema());
        return type.GetProperties().Select(p => p.GetColumnName(table)!).ToList();
    }

    [Fact]
    public void Tables_AreRunsRunStepsAndPrompts()
    {
        var model = Model();

        Assert.Equal("runs", model.FindEntityType(typeof(Run))!.GetTableName());
        Assert.Equal("run_steps", model.FindEntityType(typeof(RunStep))!.GetTableName());
        Assert.Equal("prompts", model.FindEntityType(typeof(Prompt))!.GetTableName());
    }

    [Fact]
    public void Runs_HoldTheReplayAndEvalData() =>
        Assert.Superset(
            new HashSet<string>
            {
                "id", "workflow_id", "workflow_run_id", "workflow_type", "status", "input_event", "context", "prompt_hash",
                "model", "started_at", "completed_at", "duration_ms", "input_tokens", "output_tokens", "cost_usd",
                "outcome", "outcome_detail", "outcome_recorded_at",
            },
            Columns(typeof(Run)).ToHashSet());

    [Fact]
    public void RunSteps_HoldEachLadderStep() =>
        Assert.Superset(
            new HashSet<string>
            {
                "id", "run_id", "seq", "name", "rung", "prompt_hash", "model", "input", "output", "confidence",
                "stop_reason", "input_tokens", "output_tokens", "cost_usd", "started_at", "duration_ms",
            },
            Columns(typeof(RunStep)).ToHashSet());

    [Fact]
    public void Prompts_AreKeyedByHash()
    {
        var key = Model().FindEntityType(typeof(Prompt))!.FindPrimaryKey()!;

        Assert.Equal(["Hash"], key.Properties.Select(p => p.Name));
    }

    [Fact]
    public void WorkflowRunAndStepSequence_AreUnique()
    {
        var model = Model();

        Assert.Contains(model.FindEntityType(typeof(Run))!.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["WorkflowId", "WorkflowRunId"]));
        Assert.Contains(model.FindEntityType(typeof(RunStep))!.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["RunId", "Seq"]));
    }

    [Fact]
    public void JsonColumns_AreJsonb()
    {
        var model = Model();
        var run = model.FindEntityType(typeof(Run))!;
        var step = model.FindEntityType(typeof(RunStep))!;

        Assert.Equal("jsonb", run.FindProperty(nameof(Run.InputEvent))!.GetColumnType());
        Assert.Equal("jsonb", run.FindProperty(nameof(Run.Context))!.GetColumnType());
        Assert.Equal("jsonb", step.FindProperty(nameof(RunStep.Input))!.GetColumnType());
        Assert.Equal("jsonb", step.FindProperty(nameof(RunStep.Output))!.GetColumnType());
    }
}
