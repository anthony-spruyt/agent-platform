namespace AgentPlatform.Host.RenovateTriage;

public sealed class RenovateTriageOptions
{
    public const string SectionName = "RenovateTriage";

    public TriageMode Mode { get; set; } = TriageMode.Shadow;

    public MigrationOptions? Migration { get; set; }

    public ClaudeBudgetOptions Claude { get; set; } = new();

    public SweeperOptions Sweeper { get; set; } = new();
}

public sealed class SweeperOptions
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class ClaudeBudgetOptions
{
    public int MaxPerHour { get; set; } = 2;

    public int MaxPerDay { get; set; } = 8;

    public int MaxPerPrPerDay { get; set; } = 2;
}

public sealed class MigrationOptions
{
    public static readonly string[] DefaultPathGlobs = ["talos/**", "**/topf.yaml"];

    public static readonly string[] DefaultDependencyGlobs = ["siderolabs/*", "kubelet", "kube-apiserver", "kubernetes/*"];

    // Nullable so a configured list replaces the default instead of binding on top of it
    public string[]? PathGlobs { get; set; }

    public string[]? DependencyGlobs { get; set; }
}
