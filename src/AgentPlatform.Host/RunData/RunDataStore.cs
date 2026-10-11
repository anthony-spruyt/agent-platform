using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Host.RunData;

public sealed class RunDataStore(IDbContextFactory<RunDataDbContext> factory)
{
    public Task<BeginRunResult> BeginRunAsync(string workflowId, string workflowRunId, BeginRunInput input, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkflowType);
        workflowId = JsonRedactor.RedactText(workflowId);
        workflowRunId = JsonRedactor.RedactText(workflowRunId);
        var workflowType = JsonRedactor.RedactText(input.WorkflowType);
        var model = Text(input.Model);
        var promptName = Text(input.PromptName);
        var promptBody = Text(input.PromptBody);
        if (input.PromptBody is not null && string.IsNullOrWhiteSpace(input.PromptName))
        {
            throw new ArgumentException("A prompt needs a name.", nameof(input));
        }

        var inputEvent = RawJson(input.InputEvent, nameof(input.InputEvent));
        var context = RawJson(input.Context, nameof(input.Context));
        var startedAt = input.StartedAt.ToUniversalTime();

        return ExecuteAsync(
            async db =>
            {
                var existing = await db.Runs
                    .Where(r => r.WorkflowId == workflowId && r.WorkflowRunId == workflowRunId)
                    .Select(r => new { r.Id, r.PromptHash })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    return new BeginRunResult(existing.Id, existing.PromptHash);
                }

                string? promptHash = null;
                if (promptBody is not null)
                {
                    promptHash = PromptHashing.Compute(promptBody);
                    if (!await db.Prompts.AnyAsync(p => p.Hash == promptHash, cancellationToken).ConfigureAwait(false))
                    {
                        db.Prompts.Add(new Prompt { Hash = promptHash, Name = promptName!, Body = promptBody, CreatedAt = DateTimeOffset.UtcNow });
                    }
                }

                var run = new Run
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = workflowId,
                    WorkflowRunId = workflowRunId,
                    WorkflowType = workflowType,
                    Status = RunStatus.Running,
                    InputEvent = inputEvent!,
                    Context = context,
                    PromptHash = promptHash,
                    Model = model,
                    StartedAt = startedAt,
                };
                db.Runs.Add(run);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new BeginRunResult(run.Id, promptHash);
            },
            cancellationToken);
    }

    public Task RecordStepAsync(RecordStepInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.StopReason);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Seq, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(input.DurationMs);
        if (input.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Confidence must be between 0 and 1.");
        }

        var stepInput = RawJson(input.Input, nameof(input.Input));
        var stepOutput = RawJson(input.Output, nameof(input.Output));
        var name = JsonRedactor.RedactText(input.Name);
        var stopReason = JsonRedactor.RedactText(input.StopReason);
        var stepModel = Text(input.Model);

        return ExecuteAsync(
            async db =>
            {
                if (!await db.Runs.AnyAsync(r => r.Id == input.RunId, cancellationToken).ConfigureAwait(false))
                {
                    throw new UnknownRunException(input.RunId);
                }

                if (await db.RunSteps.AnyAsync(s => s.RunId == input.RunId && s.Seq == input.Seq, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                if (input.PromptHash is not null && !await db.Prompts.AnyAsync(p => p.Hash == input.PromptHash, cancellationToken).ConfigureAwait(false))
                {
                    throw new ArgumentException("The prompt hash does not match a stored prompt.", nameof(input));
                }

                db.RunSteps.Add(new RunStep
                {
                    RunId = input.RunId,
                    Seq = input.Seq,
                    Name = name,
                    Rung = input.Rung,
                    StopReason = stopReason,
                    StartedAt = input.StartedAt.ToUniversalTime(),
                    DurationMs = input.DurationMs,
                    Input = stepInput,
                    Output = stepOutput,
                    Confidence = input.Confidence,
                    PromptHash = input.PromptHash,
                    Model = stepModel,
                    InputTokens = input.InputTokens,
                    OutputTokens = input.OutputTokens,
                    CostUsd = input.CostUsd,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task CompleteRunAsync(CompleteRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var model = Text(input.Model);

        return ExecuteAsync(
            async db =>
            {
                var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == input.RunId, cancellationToken).ConfigureAwait(false)
                    ?? throw new UnknownRunException(input.RunId);
                var steps = await db.RunSteps
                    .Where(s => s.RunId == input.RunId)
                    .Select(s => new { s.InputTokens, s.OutputTokens, s.CostUsd })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var completedAt = input.CompletedAt.ToUniversalTime();
                run.Status = input.Status;
                run.CompletedAt = completedAt;
                run.DurationMs = Math.Max(0, (long)(completedAt - run.StartedAt).TotalMilliseconds);
                run.InputTokens = steps.Any(s => s.InputTokens is not null) ? steps.Sum(s => s.InputTokens ?? 0) : null;
                run.OutputTokens = steps.Any(s => s.OutputTokens is not null) ? steps.Sum(s => s.OutputTokens ?? 0) : null;
                run.CostUsd = steps.Any(s => s.CostUsd is not null) ? steps.Sum(s => s.CostUsd ?? 0) : null;
                run.Model = model ?? run.Model;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private static string? Text(string? value) => value is null ? null : JsonRedactor.RedactText(value);

    private static string? RawJson(JsonElement? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        return RawJson(value.Value, name);
    }

    private static string RawJson(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("A JSON value is required.", name)
            : JsonRedactor.Redact(value);

    // A concurrent writer can win the unique key between the existence check and the insert: look again on a fresh context.
    private async Task<T> ExecuteAsync<T>(Func<RunDataDbContext, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await action(db).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await action(db).ConfigureAwait(false);
        }
    }
}
