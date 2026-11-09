using System.Globalization;
using AgentPlatform.Host.ClaudeBudget;

namespace AgentPlatform.Host.Livemap;

public static class LivemapBudget
{
    public const string Kind = "Livemap";

    public static ClaudeKindOptions DefaultCaps() => new() { MaxPerSubjectPerDay = 4, MaxFailedAttemptsPerRevision = 3 };

    public static ReserveClaudeRunInput Reserve(Guid runId, long buildId, string fork, string mainSha, string model) =>
        new(runId, Kind, buildId.ToString(CultureInfo.InvariantCulture), fork, mainSha, model);
}
