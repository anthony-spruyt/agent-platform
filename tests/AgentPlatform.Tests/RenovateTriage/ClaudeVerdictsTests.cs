using AgentPlatform.Host.Agents;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class ClaudeVerdictsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static TriageResult Result(string verdict, string summary = "Looks fine.", string[]? fix = null, string[]? reasons = null) =>
        new(verdict, Sha, summary, [], fix ?? [], reasons ?? []);

    [Theory]
    [InlineData("SAFE", TriageVerdict.Safe, "claude-safe", "SAFE")]
    [InlineData("FIXABLE", TriageVerdict.Fixable, "claude-fixable", "FIXABLE")]
    [InlineData("BLOCKED", TriageVerdict.Blocked, "claude-blocked", "BLOCKED")]
    [InlineData("NEEDS_REVIEW", TriageVerdict.Owner, "claude-needs-review", "NEEDS REVIEW")]
    public void EachAgentVerdict_MapsToItsStoredVerdictAndReason(string verdict, TriageVerdict expected, string reason, string label)
    {
        var decision = ClaudeVerdicts.Decision(Result(verdict), "opus", "run-1");

        Assert.Equal((expected, reason), (decision.Verdict, decision.Reason));
        Assert.StartsWith($"**Agent verdict: {label}**", decision.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComment_QuotesTheSummaryTheFixAndTheReasons_AndNamesTheHeadModelAndRun()
    {
        var decision = ClaudeVerdicts.Decision(Result("FIXABLE", "The build needs a new flag.", ["add --flag"], ["CI failed on the flag"]), "opus", "run-1");

        Assert.Contains("> The build needs a new flag.", decision.Comment, StringComparison.Ordinal);
        Assert.Contains("> What to fix:", decision.Comment, StringComparison.Ordinal);
        Assert.Contains("> - add --flag", decision.Comment, StringComparison.Ordinal);
        Assert.Contains("> Why:", decision.Comment, StringComparison.Ordinal);
        Assert.Contains("> - CI failed on the flag", decision.Comment, StringComparison.Ordinal);
        Assert.Contains("head 0123456 · model opus · run run-1", decision.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentText_IsMadeInert()
    {
        var decision = ClaudeVerdicts.Decision(Result("SAFE", "ping @owner and see #12"), null, null);

        Assert.DoesNotContain("@owner", decision.Comment, StringComparison.Ordinal);
        Assert.DoesNotContain(" #12", decision.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerdict_IsRefused() =>
        Assert.Throws<ArgumentException>(() => ClaudeVerdicts.Decision(Result("MAYBE"), "opus", "run-1"));

    [Theory]
    [InlineData(ClaudeBudgetCauses.AttemptsExhausted, "failed twice")]
    [InlineData(ClaudeBudgetCauses.BreakerOpen, "paused")]
    public void WhenTheAgentCannotRun_TheHeadGoesToTheOwnerWithTheCauseNamed(string cause, string words)
    {
        var decision = ClaudeVerdicts.Unavailable(cause);

        Assert.Equal((TriageVerdict.Owner, cause), (decision.Verdict, decision.Reason));
        Assert.Contains(words, decision.Comment, StringComparison.Ordinal);
        Assert.StartsWith(TriageRules.NeedsReviewPrefix, decision.Comment, StringComparison.Ordinal);
    }
}
