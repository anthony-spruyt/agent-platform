using AgentPlatform.Host.GitHub;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class BumpAnalyzerTests
{
    private static readonly string DigestA = "sha256:" + new string('a', 64);
    private static readonly string DigestB = "sha256:" + new string('b', 64);
    private static readonly string ShaA = new('c', 40);
    private static readonly string ShaB = new('d', 40);

    private static BumpFacts Analyze(params (string File, string? Patch)[] files) =>
        BumpAnalyzer.Analyze([.. files.Select(f => new GitHubPullRequestFile(f.File, f.Patch))]);

    private static BumpFacts AnalyzePatch(string patch) => Analyze(("file.yaml", patch));

    public class Digests
    {
        [Fact]
        public void AnImageDigestThatChanges_IsDigestOnly()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-image: ghcr.io/octo/app:1.2.3@{DigestA}\n+image: ghcr.io/octo/app:1.2.3@{DigestB}");

            Assert.True(facts.DigestOnly);
            Assert.Empty(facts.Versions);
        }

        [Fact]
        public void AGitShaPin_ThatChanges_IsDigestOnly()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-      - uses: actions/checkout@{ShaA} # v4.1.0\n+      - uses: actions/checkout@{ShaB} # v4.1.0");

            Assert.True(facts.DigestOnly);
        }

        [Fact]
        public void SeveralFilesAndHunksThatOnlyChangeDigests_AreDigestOnly()
        {
            var one = $"@@ -1 +1 @@\n-a: {DigestA}\n+a: {DigestB}\n context\n@@ -9 +9 @@\n-b: {DigestA}\n+b: {DigestB}";
            var two = $"@@ -1,2 +1,2 @@\n-x: {ShaA}\n-y: {ShaA}\n+x: {ShaB}\n+y: {ShaB}";

            Assert.True(Analyze(("a.yaml", one), ("b.yaml", two)).DigestOnly);
        }

        [Fact]
        public void ATagThatChangesBesideTheDigest_IsNotDigestOnly()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-image: app:1.2.3@{DigestA}\n+image: app:1.2.4@{DigestB}");

            Assert.False(facts.DigestOnly);
        }

        [Fact]
        public void ANonDigestLineThatChanges_IsNotDigestOnly()
        {
            var facts = AnalyzePatch($"@@ -1,2 +1,2 @@\n-image: app@{DigestA}\n-replicas: 1\n+image: app@{DigestB}\n+replicas: 2");

            Assert.False(facts.DigestOnly);
        }

        [Fact]
        public void AddedOrRemovedLines_AreNotDigestOnly()
        {
            Assert.False(AnalyzePatch($"@@ -1 +1,2 @@\n-a: {DigestA}\n+a: {DigestB}\n+extra: true").DigestOnly);
            Assert.False(AnalyzePatch($"@@ -1,2 +1 @@\n-a: {DigestA}\n-extra: true\n+a: {DigestB}").DigestOnly);
            Assert.False(AnalyzePatch("@@ -0,0 +1 @@\n+new: line").DigestOnly);
        }

        [Fact]
        public void AFileWithoutAPatch_IsNotDigestOnly()
        {
            Assert.False(Analyze(("a.yaml", $"@@ -1 +1 @@\n-a: {DigestA}\n+a: {DigestB}"), ("big.bin", null)).DigestOnly);
            Assert.False(Analyze(("big.bin", string.Empty)).DigestOnly);
        }

        [Fact]
        public void NoFilesAndNoChangedLines_AreNotDigestOnly()
        {
            Assert.False(BumpAnalyzer.Analyze([]).DigestOnly);
            Assert.False(AnalyzePatch("@@ -1 +1 @@\n context").DigestOnly);
        }

        [Fact]
        public void AHexStringOfAnotherLength_IsNotADigest()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-a: {new string('a', 39)}\n+a: {new string('b', 39)}");

            Assert.False(facts.DigestOnly);
        }

        [Fact]
        public void ADigestInsideALongerHexRun_IsNotADigest()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-a: {new string('a', 41)}\n+a: {new string('b', 41)}");

            Assert.False(facts.DigestOnly);
        }

        [Fact]
        public void TheNoNewlineMarker_IsIgnored()
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-a: {DigestA}\n\\ No newline at end of file\n+a: {DigestB}\n\\ No newline at end of file");

            Assert.True(facts.DigestOnly);
        }
    }

    public class Versions
    {
        [Theory]
        [InlineData("1.0.0", "1.0.1", "patch")]
        [InlineData("1.0.0", "1.1.0", "minor")]
        [InlineData("1.9.9", "2.0.0", "major")]
        [InlineData("v1.0.0", "v1.0.1", "patch")]
        [InlineData("1.2", "1.3", "minor")]
        [InlineData("1.2.3", "1.2.3-rc.1", "none")]
        [InlineData("1.0.0-alpine", "1.0.1-alpine", "patch")]
        [InlineData("1.0.1", "1.0.0", "patch")]
        public void TheKindIsTheFirstComponentThatDiffers(string oldVersion, string newVersion, string kind)
        {
            var facts = AnalyzePatch($"@@ -1 +1 @@\n-  version: {oldVersion}\n+  version: {newVersion}");

            if (kind == "none")
            {
                Assert.Empty(facts.Versions);
                return;
            }

            Assert.Equal(new VersionChange(oldVersion, newVersion, kind), Assert.Single(facts.Versions));
        }

        [Fact]
        public void TheSameChangeSeenTwice_IsListedOnce()
        {
            var facts = Analyze(
                ("package.json", "@@ -1 +1 @@\n-\"dep\": \"1.0.0\"\n+\"dep\": \"1.0.1\""),
                ("package-lock.json", "@@ -1 +1 @@\n-\"version\": \"1.0.0\"\n+\"version\": \"1.0.1\""));

            Assert.Single(facts.Versions);
        }

        [Fact]
        public void SeveralPackages_AreAllListed()
        {
            var facts = AnalyzePatch("@@ -1,2 +1,2 @@\n-a: 1.0.0\n-b: 2.1.0\n+a: 1.0.1\n+b: 3.0.0");

            Assert.Equal(
                [new VersionChange("1.0.0", "1.0.1", "patch"), new VersionChange("2.1.0", "3.0.0", "major")],
                facts.Versions);
        }

        [Fact]
        public void AVersionThatDoesNotChange_IsNotListed()
        {
            var facts = AnalyzePatch("@@ -1 +1 @@\n-image: app:1.2.3 # a\n+image: app:1.2.3 # b");

            Assert.Empty(facts.Versions);
        }

        [Fact]
        public void LinesWithADifferentNumberOfVersions_AreNotPaired()
        {
            var facts = AnalyzePatch("@@ -1 +1 @@\n-a: 1.0.0\n+a: 1.0.1 and 2.0.0");

            Assert.Empty(facts.Versions);
        }

        [Fact]
        public void ALineWithoutAVersion_YieldsNothing()
        {
            Assert.Empty(AnalyzePatch("@@ -1 +1 @@\n-name: old\n+name: new").Versions);
        }

        [Theory]
        [InlineData("1.2.3.4", "1.2.3.5")]
        [InlineData("py3.11", "py3.12")]
        public void VersionLikeTextThatIsNotSemver_IsIgnored(string oldText, string newText)
        {
            Assert.Empty(AnalyzePatch($"@@ -1 +1 @@\n-x: {oldText}\n+x: {newText}").Versions);
        }

        [Fact]
        public void AVersionFollowedByAFullStop_IsStillRead()
        {
            var facts = AnalyzePatch("@@ -1 +1 @@\n-Bump to 1.0.0.\n+Bump to 1.0.1.");

            Assert.Equal(new VersionChange("1.0.0", "1.0.1", "patch"), Assert.Single(facts.Versions));
        }

        [Fact]
        public void LinesAreOnlyPairedWithinTheirOwnBlock()
        {
            var facts = AnalyzePatch("@@ -1,3 +1,3 @@\n-a: 1.0.0\n+a: 1.0.1\n keep: 5.5.5\n-b: 2.0.0\n+b: 2.0.1");

            Assert.Equal(
                [new VersionChange("1.0.0", "1.0.1", "patch"), new VersionChange("2.0.0", "2.0.1", "patch")],
                facts.Versions);
        }

        [Fact]
        public void AnOverflowingNumber_IsIgnored()
        {
            var big = new string('9', 30);

            Assert.Empty(AnalyzePatch($"@@ -1 +1 @@\n-x: {big}.0.0\n+x: {big}.0.1").Versions);
        }
    }
}
