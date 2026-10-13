using System.Text.Json;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class ClaudeLimitTrackerTests
{
    private static string Assistant(string id, int input, int output, int cacheCreation = 0, int cacheRead = 0, params string[] toolUses)
    {
        var blocks = toolUses.Length == 0
            ? [new { type = "text", id = "", name = "", text = "hi" }]
            : toolUses.Select((name, i) => new { type = "tool_use", id = $"{id}-t{i}", name, text = "" }).ToArray();
        return JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id,
                content = blocks,
                usage = new
                {
                    input_tokens = input,
                    output_tokens = output,
                    cache_creation_input_tokens = cacheCreation,
                    cache_read_input_tokens = cacheRead,
                },
            },
        });
    }

    [Fact]
    public void Observe_BelowTheCeiling_ReportsNothing()
    {
        var tracker = new ClaudeLimitTracker(1000, 3);

        Assert.Null(tracker.Observe(Assistant("m1", 100, 100)));
        Assert.Null(tracker.Observe(Assistant("m2", 100, 100)));
    }

    [Fact]
    public void Observe_WhenStreamedUsageCrossesTheCeiling_ReportsTheTokenLimit()
    {
        var tracker = new ClaudeLimitTracker(1000, 3);

        Assert.Null(tracker.Observe(Assistant("m1", 400, 100)));
        var breach = tracker.Observe(Assistant("m2", 400, 101));

        Assert.NotNull(breach);
        Assert.Contains("token", breach, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Observe_CountsAMessageOnceWhenItsUsageIsRepeatedAcrossContentBlocks()
    {
        var tracker = new ClaudeLimitTracker(1000, 3);

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(tracker.Observe(Assistant("m1", 300, 100)));
        }
    }

    [Fact]
    public void Observe_CountsCacheCreationButNotCacheReads()
    {
        var tracker = new ClaudeLimitTracker(1000, 3);

        Assert.Null(tracker.Observe(Assistant("m1", 1, 1, cacheCreation: 500, cacheRead: 900_000)));
        Assert.NotNull(tracker.Observe(Assistant("m2", 1, 1, cacheCreation: 500)));
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void Observe_WhenMoreThanTheAllowedSubagentsAreStarted_ReportsTheFanOutLimit(string tool)
    {
        var tracker = new ClaudeLimitTracker(1_000_000, 3);

        Assert.Null(tracker.Observe(Assistant("m1", 1, 1, toolUses: [tool, tool])));
        Assert.Null(tracker.Observe(Assistant("m2", 1, 1, toolUses: [tool])));
        var breach = tracker.Observe(Assistant("m3", 1, 1, toolUses: [tool]));

        Assert.NotNull(breach);
        Assert.Contains("subagent", breach, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Observe_CountsARepeatedToolUseEventOnce()
    {
        var tracker = new ClaudeLimitTracker(1_000_000, 1);

        Assert.Null(tracker.Observe(Assistant("m1", 1, 1, toolUses: ["Task"])));
        Assert.Null(tracker.Observe(Assistant("m1", 1, 1, toolUses: ["Task"])));
    }

    [Fact]
    public void Observe_IgnoresOtherToolsAndNonAssistantLines()
    {
        var tracker = new ClaudeLimitTracker(10, 0);

        Assert.Null(tracker.Observe(Assistant("m0", 1, 1, toolUses: ["Bash", "Read"])));
        Assert.Null(tracker.Observe("""{"type":"result","usage":{"input_tokens":999999,"output_tokens":999999}}"""));
        Assert.Null(tracker.Observe("not json"));
        Assert.Null(tracker.Observe(""));
        Assert.Null(tracker.Observe("[1,2]"));
    }
}
