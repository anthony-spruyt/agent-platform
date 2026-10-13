using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class MigrationDetectorTests
{
    private static readonly MigrationDetector _defaults = new(new RenovateTriageOptions());

    [Theory]
    [InlineData("talos/talconfig.yaml")]
    [InlineData("talos/nodes/cp1/patch.yaml")]
    [InlineData("topf.yaml")]
    [InlineData("cluster/topf.yaml")]
    [InlineData("a/b/c/topf.yaml")]
    public void ChangedPaths_MatchingTheDefaultGlobs_AreHandsOn(string path)
    {
        var match = _defaults.Detect([path], []);

        Assert.Equal(path, match?.Path);
    }

    [Theory]
    [InlineData("cluster/apps/web/values.yaml")]
    [InlineData("docs/talos/readme.md")]
    [InlineData("topf.yaml.bak")]
    public void ChangedPaths_OutsideTheDefaultGlobs_AreNot(string path)
    {
        Assert.Null(_defaults.Detect([path], []));
    }

    [Theory]
    [InlineData("siderolabs/talos")]
    [InlineData("siderolabs/installer")]
    [InlineData("kubelet")]
    [InlineData("kube-apiserver")]
    [InlineData("kubernetes/kubernetes")]
    [InlineData("Kubernetes/Kubernetes")]
    public void Dependencies_MatchingTheDefaultGlobs_AreHandsOn(string dependency)
    {
        var match = _defaults.Detect([], [dependency]);

        Assert.Equal(dependency, match?.Dependency);
    }

    [Theory]
    [InlineData("kubernetes-sigs/kustomize")]
    [InlineData("siderolabs/talos/extra")]
    [InlineData("grafana/grafana")]
    public void Dependencies_OutsideTheDefaultGlobs_AreNot(string dependency)
    {
        Assert.Null(_defaults.Detect([], [dependency]));
    }

    [Fact]
    public void NoPathsAndNoDependencies_IsNotHandsOn()
    {
        Assert.Null(_defaults.Detect([], []));
    }

    [Fact]
    public void ConfiguredGlobsReplaceTheDefaults()
    {
        var options = new RenovateTriageOptions
        {
            Migration = new MigrationOptions { PathGlobs = ["infra/**"], DependencyGlobs = ["acme/*"] },
        };
        var detector = new MigrationDetector(options);

        Assert.NotNull(detector.Detect(["infra/db/main.tf"], []));
        Assert.NotNull(detector.Detect([], ["acme/db"]));
        Assert.Null(detector.Detect(["talos/talconfig.yaml"], []));
        Assert.Null(detector.Detect([], ["siderolabs/talos"]));
    }

    [Fact]
    public void ConfiguredEmptyGlobsDisableThatCheck()
    {
        var options = new RenovateTriageOptions { Migration = new MigrationOptions { PathGlobs = [], DependencyGlobs = [] } };
        var detector = new MigrationDetector(options);

        Assert.Null(detector.Detect(["talos/talconfig.yaml"], ["siderolabs/talos"]));
    }

    [Fact]
    public void AnUnsetGlobListKeepsItsDefault()
    {
        var detector = new MigrationDetector(new RenovateTriageOptions { Migration = new MigrationOptions { DependencyGlobs = ["acme/*"] } });

        Assert.NotNull(detector.Detect(["talos/talconfig.yaml"], []));
    }

    [Fact]
    public void ThePathMatchIsReportedBeforeTheDependencyMatch()
    {
        var match = _defaults.Detect(["talos/a.yaml"], ["siderolabs/talos"]);

        Assert.Equal("talos/a.yaml", match?.Path);
        Assert.Null(match?.Dependency);
    }

    [Fact]
    public void TheDecisionSendsThePullRequestToTheOwner()
    {
        var decision = MigrationDetector.Decision(new MigrationMatch("talos/a.yaml", null));

        Assert.Equal(TriageVerdict.NeedsReview, decision.Verdict);
        Assert.Equal(MigrationDetector.Reason, decision.Reason);
        Assert.Contains("talos/a.yaml", decision.Comment, StringComparison.Ordinal);
    }
}
