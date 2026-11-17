using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.Support;

internal static class TriageWaits
{
    public static TriageDecision Wait(string reason = TriageRules.CiTimeout) =>
        (reason == TriageRules.CiTimeout ? TriageRules.CiTimeoutDecision() : TriageRules.PrecheckDecision(PrecheckVerdict.BodyUnchecked)) with { Verdict = TriageVerdict.Owner };
}
