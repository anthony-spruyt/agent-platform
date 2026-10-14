using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RenovateTriage;
using AgentPlatform.Host.Workflows;
using Temporalio.Activities;

namespace AgentPlatform.Tests.Support;

internal sealed class ScriptedBudget
{
    public List<ReserveClaudeRunInput> Reserves { get; } = [];

    public List<Guid> PodsCreated { get; } = [];

    public List<FinishClaudeRunInput> Finished { get; } = [];

    public List<SetClaudeCooldownInput> Cooldowns { get; } = [];

    public List<string> Calls { get; } = [];

    // Defaults to the store's attempt rule: a head gets two failed attempts, then goes to the owner.
    public Func<ReserveClaudeRunInput, int, ClaudeReservation> OnReserve { get; set; }

    public ScriptedBudget() =>
        OnReserve = (input, _) =>
        {
            var failed = Finished.Count(f => f.Outcome.IsFailure());
            return failed >= 2
                ? ClaudeReservation.Owner(ClaudeBudgetCauses.AttemptsExhausted)
                : ClaudeReservation.Reserved(input.RunId, failed + 1);
        };

    [Activity("ReserveClaudeRun")]
    public Task<ClaudeReservation> ReserveAsync(ReserveClaudeRunInput input)
    {
        Calls.Add("reserve");
        Reserves.Add(input);
        return Task.FromResult(OnReserve(input, Reserves.Count));
    }

    [Activity("MarkClaudePodCreated")]
    public Task MarkPodCreatedAsync(Guid runId)
    {
        Calls.Add("pod-created");
        PodsCreated.Add(runId);
        return Task.CompletedTask;
    }

    [Activity("FinishClaudeRun")]
    public Task FinishAsync(FinishClaudeRunInput input)
    {
        Calls.Add($"finish:{input.Outcome}");
        Finished.Add(input);
        return Task.CompletedTask;
    }

    public Func<SetClaudeCooldownInput, DateTimeOffset?> OnCooldown { get; set; } = input => input.ResetsAt;

    [Activity("SetClaudeCooldown")]
    public Task<DateTimeOffset> SetCooldownAsync(SetClaudeCooldownInput input)
    {
        Calls.Add("cooldown");
        Cooldowns.Add(input);
        return Task.FromResult(OnCooldown(input) ?? DateTimeOffset.UnixEpoch);
    }
}

internal sealed class ScriptedPods
{
    public List<RunAgentPodInput> Inputs { get; } = [];

    public Func<RunAgentPodInput, ActivityInfo, Task<AgentRunResult>> OnRun { get; set; } = (_, _) => Task.FromResult(Succeeded());

    public static AgentRunResult Succeeded() =>
        new(AgentRunOutcome.Succeeded, "pod-1", 0, "done", null, new AgentRunUsage(100, 20, 0.5, 3, 4, "s"), "opus-test");

    public static AgentRunResult Failed(AgentRunOutcome outcome, DateTimeOffset? resetsAt = null) =>
        new(outcome, "pod-1", 1, null, "failed", null, "opus-test", resetsAt);

    // The SDK throttles heartbeats to tens of seconds, so a test releases a held pod once the cancellation request is in the history.
    public static async Task<AgentRunResult> HeldAsync(Task release)
    {
        await release;
        return Failed(AgentRunOutcome.PodFailed);
    }

    [Activity("RunAgentPod")]
    public Task<AgentRunResult> RunAgentPodAsync(RunAgentPodInput input)
    {
        Inputs.Add(input);
        return OnRun(input, ActivityExecutionContext.Current.Info);
    }
}

internal sealed class RecordingCleanup
{
    public List<string> Cleanups { get; } = [];

    [Activity("DeleteAgentPod")]
    public Task DeleteAgentPodAsync(string step)
    {
        Cleanups.Add(step);
        return Task.CompletedTask;
    }
}
