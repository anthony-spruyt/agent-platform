using System.Text.RegularExpressions;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public partial class AgentPodNamingTests
{
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabel();

    [Fact]
    public void PodName_IsDeterministic()
    {
        var first = AgentPodNaming.PodName("wf-1", "run-a", "claude");
        var second = AgentPodNaming.PodName("wf-1", "run-a", "claude");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("wf-1", "run-a", "claude")]
    [InlineData("renovate-triage/anthony-spruyt/spruyt-labs/3467", "019f1c2e-aaaa-bbbb-cccc-1234567890ab", "ladder-3")]
    [InlineData("UPPER_case.and spaces!!", "run", "step with spaces")]
    [InlineData("---", "run", "---")]
    [InlineData("é/ü/日本語", "run", "step")]
    public void PodName_IsDnsLabelOfAtMost63Chars(string workflowId, string runId, string step)
    {
        var name = AgentPodNaming.PodName(workflowId, runId, step);

        Assert.True(name.Length <= 63, name);
        Assert.Matches(DnsLabel(), name);
    }

    [Fact]
    public void PodName_IsDnsLabelForVeryLongIds()
    {
        var name = AgentPodNaming.PodName(new string('x', 500), new string('y', 500), new string('z', 500));

        Assert.True(name.Length <= 63, name);
        Assert.Matches(DnsLabel(), name);
    }

    [Theory]
    [InlineData("wf-2", "run-a", "claude")]
    [InlineData("wf-1", "run-b", "claude")]
    [InlineData("wf-1", "run-a", "jev")]
    public void PodName_DiffersWhenAnyInputDiffers(string workflowId, string runId, string step)
    {
        var baseline = AgentPodNaming.PodName("wf-1", "run-a", "claude");

        Assert.NotEqual(baseline, AgentPodNaming.PodName(workflowId, runId, step));
    }

    [Fact]
    public void PodName_DiffersForIdsThatSanitiseIdentically()
    {
        Assert.NotEqual(
            AgentPodNaming.PodName("a/b", "run", "s"),
            AgentPodNaming.PodName("a-b", "run", "s"));
    }

    [Fact]
    public void PodName_DoesNotAmbiguouslyJoinFields()
    {
        Assert.NotEqual(
            AgentPodNaming.PodName("a", "b-c", "d"),
            AgentPodNaming.PodName("a", "b", "c-d"));
    }
}
