using System.Text;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Host.RenovateTriage;

public static class ClaudeVerdicts
{
    public static TriageDecision Decision(TriageResult result, string? model, string? runId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var (verdict, reason) = result.Verdict switch
        {
            "SAFE" => (TriageVerdict.Safe, "claude-safe"),
            "FIXABLE" => (TriageVerdict.Fixable, "claude-fixable"),
            "BLOCKED" => (TriageVerdict.Blocked, "claude-blocked"),
            "NEEDS_REVIEW" => (TriageVerdict.Owner, "claude-needs-review"),
            _ => throw new ArgumentException($"Unknown agent verdict {result.Verdict}.", nameof(result)),
        };
        return new TriageDecision(verdict, reason, StickyComment.Verdict(verdict, AgentText(result), result.HeadSha, model, runId));
    }

    public static TriageDecision Unavailable(string cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cause);
        var why = cause switch
        {
            ClaudeBudgetCauses.AttemptsExhausted => "the agent review failed twice on this commit",
            ClaudeBudgetCauses.BreakerOpen => "agent reviews for this repository are paused after repeated failures",
            _ => "the agent review is not available",
        };
        return new TriageDecision(TriageVerdict.Owner, cause, $"{TriageRules.NeedsReviewPrefix} {why}.");
    }

    private static string AgentText(TriageResult result)
    {
        var text = new StringBuilder(result.Summary);
        AppendList(text, "What to fix:", result.Fix);
        AppendList(text, "Why:", result.Reasons);
        return text.ToString();
    }

    private static void AppendList(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append("\n\n").Append(heading);
        foreach (var item in items)
        {
            text.Append("\n- ").Append(item);
        }
    }
}
