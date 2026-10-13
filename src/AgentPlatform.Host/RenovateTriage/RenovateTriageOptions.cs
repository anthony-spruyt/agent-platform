namespace AgentPlatform.Host.RenovateTriage;

public sealed class RenovateTriageOptions
{
    public const string SectionName = "RenovateTriage";

    public TriageMode Mode { get; set; } = TriageMode.Shadow;

    public MigrationOptions? Migration { get; set; }
}

public sealed class MigrationOptions
{
    public static readonly string[] DefaultPathGlobs = ["talos/**", "**/topf.yaml"];

    public static readonly string[] DefaultDependencyGlobs = ["siderolabs/*", "kubelet", "kube-apiserver", "kubernetes/*"];

    // Nullable so a configured list replaces the default instead of binding on top of it
    public string[]? PathGlobs { get; set; }

    public string[]? DependencyGlobs { get; set; }
}
