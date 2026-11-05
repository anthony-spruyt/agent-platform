using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Workflows;
using Temporalio.Converters;

namespace AgentPlatform.Tests.Workflows;

public class RunAgentPodInputPayloadTests
{
    private static readonly string Sha = new('a', 40);

    private static T RoundTrip<T>(object value)
    {
        var converter = DataConverter.Default.PayloadConverter;
        return converter.ToValue<T>(converter.ToPayload(value));
    }

    [Fact]
    public void AnInputRecordedBeforeTheOverridesExisted_ReadsAsARunWithTodaysDefaults()
    {
        var recorded = new { Step = "claude", Prompt = "p", Tier = AgentTier.Read, DeadlineSeconds = 1800, Attempt = 2, Repo = new { Repo = "octo/widgets", PullRequest = 7, HeadSha = Sha } };

        var input = RoundTrip<RunAgentPodInput>(recorded);

        Assert.Equal(new RunAgentPodInput("claude", "p", AgentTier.Read, 1800, 2, new AgentRepoContext("octo/widgets", 7, Sha)), input);
        Assert.Null(input.Overrides);
    }

    [Fact]
    public void AnInputWithoutARepoOrOverrides_ReadsBackAsItWasSent()
    {
        var sent = new RunAgentPodInput("claude", "p", AgentTier.ClusterRead, 600);

        Assert.Equal(sent, RoundTrip<RunAgentPodInput>(sent));
    }

    [Fact]
    public void AnInputWithOverridesAndNoPullRequest_ReadsBackWithEveryValue()
    {
        var overrides = new AgentRunOverrides("opus", 90, 7.5m, new Dictionary<string, string> { ["contents"] = "read" });
        var sent = new RunAgentPodInput("claude", "p", AgentTier.Read, 600, 1, new AgentRepoContext("octo/widgets", null, Sha), overrides);

        var received = RoundTrip<RunAgentPodInput>(sent);

        Assert.Null(received.Repo!.PullRequest);
        Assert.Equal(sent.Repo, received.Repo);
        Assert.Equal(("opus", 90, 7.5m), (received.Overrides!.Model, received.Overrides.MaxTurns, received.Overrides.MaxBudgetUsd));
        Assert.Equal(overrides.TokenPermissions, received.Overrides.TokenPermissions);
    }
}
