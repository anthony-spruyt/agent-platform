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
            new WorkflowReplayerOptions().AddWorkflow<PingWorkflow>().AddWorkflow<SpikeWorkflow>());
        var history = WorkflowHistory.FromJson(name, HistoryStore.Read(name));

        var result = await replayer.ReplayWorkflowAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.ReplayFailure);
    }

    [Theory]
    [InlineData("ping-")]
    [InlineData("spike-")]
    public void EveryWorkflowHasAtLeastOneCommittedHistory(string prefix) =>
        Assert.Contains(HistoryStore.Names(), name => name.StartsWith(prefix, StringComparison.Ordinal));
}
