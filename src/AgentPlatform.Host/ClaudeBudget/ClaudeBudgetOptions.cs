namespace AgentPlatform.Host.ClaudeBudget;

public sealed class ClaudeBudgetOptions
{
    public const string SectionName = "ClaudeBudget";

    public int MaxPerHour { get; set; } = 2;

    public int MaxPerDay { get; set; } = 8;

    public Dictionary<string, ClaudeKindOptions> Kinds { get; set; } = new(StringComparer.Ordinal);

    public ClaudeKindOptions For(string kind) => Kinds.TryGetValue(kind, out var options) ? options : new ClaudeKindOptions();
}

public sealed class ClaudeKindOptions
{
    public int MaxPerSubjectPerDay { get; set; } = 2;

    public int MaxFailedAttemptsPerRevision { get; set; } = 2;
}
