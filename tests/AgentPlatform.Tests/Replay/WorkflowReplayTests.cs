using System.Text.Json.Nodes;
using AgentPlatform.Host.Workflows;
using Temporalio.Common;
using Temporalio.Worker;

namespace AgentPlatform.Tests.Replay;

public class WorkflowReplayTests
{
    public static TheoryData<string> HistoryFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in HistoryStore.Names())
        {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HistoryFiles))]
    public async Task CommittedHistory_ReplaysWithoutNondeterminism(string name)
    {
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<PingWorkflow>().AddWorkflow<RenovateTriageWorkflow>().AddWorkflow<RenovateTriageSweeperWorkflow>());
        var history = WorkflowHistory.FromJson(name, HistoryStore.Read(name));

        var result = await replayer.ReplayWorkflowAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.ReplayFailure);
    }

    [Fact]
    public async Task APreClaudeHistory_WhoseContextBuildFailed_ReplaysThroughTheStepThatRecordsTheError()
    {
        const string name = "renovate-triage-step2-context";
        var history = JsonNode.Parse(HistoryStore.Read(name))!;
        var events = history["events"]!.AsArray();
        var scheduled = events.Single(e => e!["activityTaskScheduledEventAttributes"]?["activityType"]?["name"]?.GetValue<string>() == "RenovateTriageBuildContext")!;
        var scheduledId = scheduled["eventId"]!.GetValue<string>();
        var completed = events.Single(e => e!["activityTaskCompletedEventAttributes"]?["scheduledEventId"]?.GetValue<string>() == scheduledId)!;
        var started = events.Single(e => e!["activityTaskStartedEventAttributes"]?["scheduledEventId"]?.GetValue<string>() == scheduledId)!;
        completed["eventType"] = "EVENT_TYPE_ACTIVITY_TASK_FAILED";
        completed.AsObject().Remove("activityTaskCompletedEventAttributes");
        completed["activityTaskFailedEventAttributes"] = new JsonObject
        {
            ["failure"] = new JsonObject
            {
                ["message"] = "the scanner is down",
                ["applicationFailureInfo"] = new JsonObject { ["type"] = "InvalidOperationException", ["nonRetryable"] = true },
            },
            ["scheduledEventId"] = scheduledId,
            ["startedEventId"] = started["eventId"]!.GetValue<string>(),
            ["retryState"] = "RETRY_STATE_NON_RETRYABLE_FAILURE",
        };
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<RenovateTriageWorkflow>());

        var result = await replayer.ReplayWorkflowAsync(WorkflowHistory.FromJson(name, history.ToJsonString()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.ReplayFailure);
    }

    [Theory]
    [InlineData("ping-")]
    [InlineData("renovate-triage-")]
    [InlineData("renovate-triage-sweeper-")]
    public void EveryWorkflowHasAtLeastOneCommittedHistory(string prefix) =>
        Assert.Contains(HistoryStore.Names(), name => name.StartsWith(prefix, StringComparison.Ordinal));
}
