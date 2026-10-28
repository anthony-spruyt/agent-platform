using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.PullRequests;

public class DiffShapeTests
{
    private const string Head = "h1";
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DefaultSubject = "chore(deps): update x";

    private static readonly string _digestOne = "sha256:" + new string('1', 64);
    private static readonly string _digestTwo = "sha256:" + new string('2', 64);

    private static string Patch(params (string Old, string New)[] lines) =>
        "@@ -1,1 +1,1 @@\n" + string.Join("\n", lines.Select(l => $"-{l.Old}\n+{l.New}"));

    // "from>to" pairs joined by "|"; null means the commit carries no table.
    private static string Message(string subject, string? rows, string datasource = "docker")
    {
        if (rows is null)
        {
            return subject;
        }

        var body = rows.Split('|').Select(r => r.Split('>')).Select(p => $"| {datasource} | p | {p[0]} | {p[1]} |");
        return subject + "\n\n| datasource | package | from | to |\n| --- | --- | --- | --- |\n" + string.Join("\n", body);
    }

    private static DiffShapeResult Classify(
        DiffFile[] files,
        string? message = null,
        string headRead = Head,
        string headReread = Head,
        DiffCommit[]? commits = null,
        int? expectedCount = null) =>
        DiffShape.Classify(
            files,
            expectedCount ?? files.Length,
            headRead,
            commits ?? [new DiffCommit(Head, message ?? DefaultSubject)],
            headReread,
            (commits ?? [new DiffCommit(Head, message ?? DefaultSubject)]).LastOrDefault()?.Message ?? string.Empty);

    private static DiffShapeResult Line(string oldLine, string newLine, string? rows = null, string subject = DefaultSubject, string datasource = "docker") =>
        Classify([new DiffFile("values.yaml", "modified", Patch((oldLine, newLine)))], Message(subject, rows, datasource));

    private static DiffShapeResult Lines(string? rows, string subject, params (string Old, string New)[] lines) =>
        Classify([new DiffFile("values.yaml", "modified", Patch(lines))], Message(subject, rows));

    [Theory]
    [InlineData("digest")]
    [InlineData("sha")]
    public void DigestAndShaSwaps_Pass(string kind)
    {
        var result = kind == "digest"
            ? Line($"tag: 1.2.3@{_digestOne}", $"tag: 1.2.3@{_digestTwo}")
            : Line("ref: " + ShaA, "ref: " + ShaB);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("tag: 1.2.3", "tag: 1.2.4", "1.2.3>1.2.4")]
    [InlineData("image: r/x:1.2.3-debian-12-r4", "image: r/x:1.2.4-debian-12-r5", "1.2.3-debian-12-r4>1.2.4-debian-12-r5")]
    [InlineData("image: r/x:4", "image: r/x:5", "4.1>5.0")]
    [InlineData("image: r/x:v4", "image: r/x:v5", "v4.1>5.0")]
    [InlineData("a: v0.59.0", "a: v0.60.0", "0.59.0>0.60.0")]
    [InlineData("a: 0.59.0", "a: 0.60.0", "v0.59.0>v0.60.0")]
    public void AVersionSwap_PassesWhenATableRowStatesIt(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows).Passed);
    }

    [Theory]
    [InlineData("cpu: \"1.5\"", "cpu: \"2.5\"")]
    [InlineData("ratio: 0.8", "ratio: 0.9")]
    [InlineData("ip: 203.0.113.1", "ip: 203.0.113.2")]
    [InlineData("cidr: 203.0.113.0/24", "cidr: 198.51.100.0/24")]
    public void ANumberThatIsNotTheStatedVersion_Fails(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "1.2.3>1.2.4").Failure);
    }

    [Theory]
    [InlineData("runAsUser: 1000", "runAsUser: 999")]
    [InlineData("port 8080", "port 9090")]
    [InlineData("image: bitnami/kubectl:1.30", "image: alpine/kubectl:1.31")]
    [InlineData("image: a.io/x:1.30", "image: b.io/x:1.31")]
    [InlineData("enabled: false", "enabled: true")]
    [InlineData("image: r/x:latest", "image: r/x:stable")]
    public void ATokenThatIsNoVersionAtAll_IsNotAVersionChange(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line(oldLine, newLine, "1.30>1.31").Failure);
    }

    [Theory]
    [InlineData("x: 12345678", "x: 12345679")]
    [InlineData("x: 1234567", "x: 7654321")]
    public void AllDigitTokensOfHexLength_AreNotShas(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line(oldLine, newLine).Failure);
    }

    [Fact]
    public void AShaSwapThatChangesLength_Fails()
    {
        var result = Line("ref: abc1234", "ref: abc12345");

        Assert.Equal(DiffShapeFailure.HexLengthChanged, result.Failure);
    }

    [Fact]
    public void AShortShaSwapOfTheSameLength_Passes()
    {
        Assert.True(Line("ref: abc1234", "ref: def5678").Passed);
    }

    [Fact]
    public void AGroupedPr_PassesWhenEveryPairMatchesARow()
    {
        var result = Lines("1.0.0>1.1.0|2.0.0>2.0.1", DefaultSubject, ("a: 1.0.0", "a: 1.1.0"), ("b: 2.0.0", "b: 2.0.1"));

        Assert.True(result.Passed);
    }

    [Fact]
    public void APairNotInTheTable_Fails()
    {
        var result = Line("a: 1.0.0", "a: 1.2.0", "1.0.0>1.1.0");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void AMajorBumpPassesOnlyAsARow()
    {
        Assert.True(Line("a: 4.9.1", "a: 5.0.0", "4.9.1>5.0.0").Passed);
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("a: 4.9.1", "a: 5.0.0", "4.9.0>5.0.0").Failure);
    }

    [Fact]
    public void TheStatedVersions_AreReadFromTheGivenMessage_NotFromTheHeadCommit()
    {
        var files = new[] { new DiffFile("values.yaml", "modified", Patch(("a: 1.0.0", "a: 1.1.0"))) };
        DiffCommit[] commits = [new DiffCommit(ShaA, Message(DefaultSubject, "1.0.0>1.1.0")), new DiffCommit(Head, "Merge branch 'main' into renovate/x")];

        var fromHead = DiffShape.Classify(files, 1, Head, commits, Head, commits[^1].Message);
        var fromRenovate = DiffShape.Classify(files, 1, Head, commits, Head, commits[0].Message);

        Assert.Equal(DiffShapeFailure.VersionNotStated, fromHead.Failure);
        Assert.True(fromRenovate.Passed);
    }

    [Fact]
    public void ALockfilePair_NeverAuthorisesTheSamePairElsewhere()
    {
        var files = new[]
        {
            new DiffFile("package-lock.json", "modified", Patch(("\"a\": \"1.0.0\"", "\"a\": \"1.1.0\""))),
            new DiffFile("config.yaml", "modified", Patch(("a: 1.0.0", "a: 1.1.0"))),
        };

        Assert.Equal(DiffShapeFailure.VersionNotStated, Classify(files, Message(DefaultSubject, null)).Failure);
    }

    [Fact]
    public void WithNoTable_TheSubjectVersionAuthorisesTheNewValue()
    {
        Assert.True(Line("a: 8.21.0", "a: 8.22.0", subject: "chore(deps): update a to 8.22.0").Passed);
        Assert.True(Line("FROM python:3.13-slim", "FROM python:3.14-slim", subject: "chore(deps): update python to 3.14-slim").Passed);
        Assert.True(Line("a: v0.59.0", "a: v0.60.0", subject: "chore(deps): update a to v0.60.0 [security]").Passed);
    }

    [Fact]
    public void WithNoTable_AMajorOnlySubjectMatchesOnlyASinglePartToken()
    {
        var comment = Line($"uses: o/a@{ShaA} # v4", $"uses: o/a@{ShaB} # v5", subject: "chore(deps): update o/a action to v5");
        var multi = Line("a: 4.9.1", "a: 5.0.0", subject: "chore(deps): update a to v5");

        Assert.True(comment.Passed);
        Assert.Equal(DiffShapeFailure.VersionNotStated, multi.Failure);
    }

    [Fact]
    public void WithNoTableAndNoSubjectVersion_AnyVersionChangeFails()
    {
        var result = Line("a: 1.0.0", "a: 1.1.0", subject: "chore(deps): bump stuff");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void AnEditedPrTitleOrBranch_IsNeverRead()
    {
        var result = Line("a: 1.0.0", "a: 1.1.0", "2.0.0>2.1.0", subject: "chore(deps): update a to 1.1.0");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Theory]
    [InlineData("| package | to |\n|-|-|\n| p | 1.1.0 |")]
    [InlineData("| datasource | package | from | to |\n| - | - | - | - |\n| docker | p | 1.0.0 |")]
    public void ATableWithMissingHeadersOrWrongCellCount_Fails(string table)
    {
        var message = DefaultSubject + "\n\n" + table;
        var result = Classify([new DiffFile("v.yaml", "modified", Patch(("a: 1.0.0", "a: 1.1.0")))], message);

        Assert.Equal(DiffShapeFailure.MalformedTable, result.Failure);
    }

    [Fact]
    public void ATableIsReadByHeaderName_WhateverTheColumnOrder()
    {
        var message = DefaultSubject + "\n\n| package | to | from | datasource |\n|---|---|---|---|\n| p | 1.1.0 | 1.0.0 | docker |";

        Assert.True(Classify([new DiffFile("v.yaml", "modified", Patch(("a: 1.0.0", "a: 1.1.0")))], message).Passed);
    }

    [Theory]
    [InlineData("uses: o/a@{A} # v4", "uses: o/a@{B} # v5", "v4.2.0>v5.0.0", DiffShapeFailure.None)]
    [InlineData("uses: \"o/a@{A}\" # v4", "uses: \"o/a@{B}\" # v5", "v4.2.0>v5.0.0", DiffShapeFailure.None)]
    [InlineData("uses: o/a@{A} # v4", "uses: o/a@{A} # v5", "v4.2.0>v5.0.0", DiffShapeFailure.CommentWithoutShaChange)]
    [InlineData("uses: o/a@{A} # v4", "uses: o/a@{B} # v6", "v4.2.0>v5.0.0", DiffShapeFailure.VersionNotStated)]
    [InlineData("uses: o/a@{A} # v4", "uses: o/a@{B} # v4", null, DiffShapeFailure.None)]
    [InlineData("uses: o/a@{A} # v2.21.0", "uses: o/a@{B} # v2.21.1", "2.21.0>2.21.1", DiffShapeFailure.None)]
    [InlineData("uses: o/a@{A} # v2.21.0", "uses: o/a@{A} # v2.21.1", "2.21.0>2.21.1", DiffShapeFailure.CommentWithoutShaChange)]
    [InlineData("uses: o/a@{A} # v1.2.3-node20", "uses: o/a@{B} # v1.2.4-node20", "v1.2.3>v1.2.4", DiffShapeFailure.VersionNotStated)]
    public void APinnedActionComment_ChangesOnlyTogetherWithItsSha(string oldLine, string newLine, string? rows, DiffShapeFailure expected)
    {
        var result = Line(oldLine.Replace("{A}", ShaA, StringComparison.Ordinal), newLine.Replace("{B}", ShaB, StringComparison.Ordinal).Replace("{A}", ShaA, StringComparison.Ordinal), rows);

        Assert.Equal(expected, result.Failure);
    }

    [Theory]
    [InlineData("- \"22:22\"", "- \"22:24\"", "22.11.0>24.0.0", "chore(deps): update x")]
    [InlineData("RUN chown 1000:1000", "RUN chown 1000:1001", null, "chore(deps): update x to v1001")]
    [InlineData("12:30", "12:45", null, "chore(deps): update x to v45")]
    [InlineData("203.0.113.1:8080", "203.0.113.1:9", null, "chore(deps): update x to v9")]
    public void ADigitOnlyNameBeforeAColon_IsNotAnImageReference(string oldLine, string newLine, string? rows, string subject)
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line(oldLine, newLine, rows, subject).Failure);
    }

    [Theory]
    [InlineData("FROM node:22", "FROM node:24", "v22>v24")]
    [InlineData("image: ghcr.io/a/b:1.2.3", "image: ghcr.io/a/b:1.2.4", "1.2.3>1.2.4")]
    [InlineData("image: reg.io:5000/a/b:1.2.3", "image: reg.io:5000/a/b:1.2.4", "1.2.3>1.2.4")]
    public void ARealImageReferenceTag_StillCounts(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows).Passed);
    }

    [Theory]
    [InlineData("digest: {1}", "digest: {2}")]
    [InlineData("digest: \"{1}\"", "digest: \"{2}\"")]
    [InlineData("image: node:22@{1}", "image: node:22@{2}")]
    public void ADigestOnlyBump_PassesWithNoRow(string oldLine, string newLine)
    {
        var result = Line(
            oldLine.Replace("{1}", _digestOne, StringComparison.Ordinal),
            newLine.Replace("{2}", _digestTwo, StringComparison.Ordinal));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ADigestLengthChange_IsNotAVersionChange()
    {
        var result = Line($"digest: {_digestOne}", $"digest: {_digestTwo}0");

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void AnImageNameSwappedBesideADigestChange_IsNotAVersionChange()
    {
        var result = Line($"image: node@{_digestOne}", $"image: evil@{_digestTwo}");

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void ASha512ThatIsNotAFullIntegrityHash_IsNotADigest()
    {
        var result = Line("url: https://h/sha512-AA/v1/get", "url: https://h/sha512-AA/v2/evil+x");

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void AFullSha512IntegrityHash_IsADigest()
    {
        var oldHash = "sha512-" + new string('A', 86) + "==";
        var newHash = "sha512-" + new string('B', 86) + "==";

        Assert.True(Line($"integrity: {oldHash}", $"integrity: {newHash}").Passed);
    }

    [Fact]
    public void ASinglePartTagNeedsAMatchingRow()
    {
        Assert.True(Line("image: r/x:4", "image: r/x:5", "4.1>5.0").Passed);
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("image: r/x:4", "image: r/x:5", "6.1>7.0").Failure);
    }

    [Fact]
    public void ASinglePartTagWithASuffix_MustEqualTheRowsMajorExactly()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("image: r/x:22-alpine", "image: r/x:24-alpine", "22.1>24.0").Failure);
    }

    [Fact]
    public void ASuffixedMultiPartTag_PassesWhenTheTableStatesTheVersionWithoutTheSuffix()
    {
        var result = Lines(
            "10.0.12>10.0.13|10.0.401>10.0.402",
            DefaultSubject,
            ($"FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@{_digestOne}", $"FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled@{_digestTwo}"),
            ($"FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@{_digestOne}", $"FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@{_digestTwo}"));

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("image: r/x:3.12.1-alpine", "image: r/x:3.12.2-alpine", "3.12.1>3.12.2")]
    [InlineData("image: r/x:v3.12.1-slim", "image: r/x:v3.12.2-slim", "3.12.1>3.12.2")]
    [InlineData("image: r/x:3.12.1-alpine3.20", "image: r/x:3.12.2-alpine3.20", "v3.12.1>v3.12.2")]
    public void ASuffixedMultiPartTag_PassesWithTheSameSuffixOnBothSides(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows).Passed);
    }

    [Theory]
    [InlineData("image: r/x:10.0.401-noble", "image: r/x:10.0.402-jammy")]
    [InlineData("image: r/x:10.0.401-noble", "image: r/x:10.0.403-noble")]
    [InlineData("image: r/x:10.0.400-noble", "image: r/x:10.0.402-noble")]
    [InlineData("image: r/x:10.0.401", "image: r/x:10.0.402-noble")]
    [InlineData("image: r/x:10.0.401-noble", "image: r/x:10.0.402")]
    [InlineData("image: r/x:10.0.401+b1", "image: r/x:10.0.402+b1")]
    [InlineData("image: r/x:10.0.401-noble", "image: r/x:10.0.402-noble-chiseled")]
    public void ASuffixedMultiPartTag_FailsWhenTheSuffixOrNumberDoesNotMatchTheRow(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "10.0.401>10.0.402").Failure);
    }

    [Fact]
    public void ASuffixedMultiPartTag_FailsWhenTheRowIsNotMultiPart()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("image: r/x:22.1-alpine", "image: r/x:24.0-alpine", "22>24").Failure);
    }

    [Fact]
    public void ASuffixedNonDockerVersionOutsideAnImageTag_MustEqualTheRowExactly()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("version: 1.2.3-rc.1", "version: 1.2.4-rc.1", "1.2.3>1.2.4", datasource: "github-releases").Failure);
    }

    [Fact]
    public void ASuffixedMultiPartTag_DoesNotRelaxTheSubjectLinePath()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("image: r/x:1.2.3-alpine", "image: r/x:1.2.4-alpine", subject: "chore(deps): update x to 1.2.4").Failure);
    }

    [Fact]
    public void ASinglePartNumberUnderAKey_IsNotAVersion()
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line("tag: 4", "tag: 5", "4.1>5.0").Failure);
    }

    [Theory]
    [InlineData("python3.13", "python3.14")]
    [InlineData("a: 1.2.3.", "a: 1.2.4.")]
    [InlineData("a: 1.2.3_x", "a: 1.2.4_x")]
    [InlineData("a: 1.2.3a", "a: 1.2.4a")]
    [InlineData("a: 1.2.3.post", "a: 1.2.4.post")]
    [InlineData("a: 1.2.3.dev", "a: 1.2.4.dev")]
    [InlineData("a: 1.2.3c1", "a: 1.2.4c1")]
    [InlineData("a: 1.2.3.post1.dev2", "a: 1.2.4.post1.dev2")]
    public void ANumberTouchingALetterDigitUnderscoreOrDot_IsNotAVersionToken(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line(oldLine, newLine, "1.2.3>1.2.4|3.13>3.14").Failure);
    }

    [Fact]
    public void AVersionPrecededByADash_IsAToken()
    {
        Assert.True(Line("name: foo-1.2.3", "name: foo-1.2.4", "1.2.3>1.2.4").Passed);
    }

    [Fact]
    public void AnUnstatedVersionInOneLine_FailsEvenWhenAnotherMatches()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("a: 1.2.3 b: 9.9.9", "a: 1.2.4 b: 9.9.8", "1.2.3>1.2.4").Failure);
    }

    [Fact]
    public void RemovedAndAddedLinesPairWithinEachContiguousRun_NotAcrossTheHunk()
    {
        var patch = "@@ -1,5 +1,5 @@\n-a: 1.0.0\n+a: 1.1.0\n unchanged\n-b: 2.0.0\n-c: 3.0.0\n+c: 3.0.1\n+b: 2.0.1";
        var message = Message(DefaultSubject, "1.0.0>1.1.0|2.0.0>2.0.1|3.0.0>3.0.1");

        var result = Classify([new DiffFile("v.yaml", "modified", patch)], message);

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void ARunWithoutAMatchingCount_IsUnpaired()
    {
        var patch = "@@ -1,2 +1,1 @@\n-a: 1.0.0\n-b: x\n+a: 1.1.0";
        var result = Classify([new DiffFile("v.yaml", "modified", patch)], Message(DefaultSubject, "1.0.0>1.1.0"));

        Assert.Equal(DiffShapeFailure.UnpairedLines, result.Failure);
    }

    [Fact]
    public void ARunsOwnChangesStartNewPairing_AfterAContextLineAndAtEachHunk()
    {
        var patch = "@@ -1,2 +1,2 @@\n-a: 1.0.0\n+a: 1.1.0\n context\n@@ -9,2 +9,2 @@\n-b: 2.0.0\n+b: 2.0.1\n\\ No newline at end of file";
        var result = Classify([new DiffFile("v.yaml", "modified", patch)], Message(DefaultSubject, "1.0.0>1.1.0|2.0.0>2.0.1"));

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("added", "go.sum")]
    [InlineData("removed", "package-lock.json")]
    [InlineData("renamed", "go.sum")]
    [InlineData("added", "values.yaml")]
    [InlineData("renamed", "other.yaml")]
    [InlineData("changed", "values.yaml")]
    public void AFileThatIsNotModified_Fails_EvenAsALockfile(string status, string path)
    {
        var result = Classify([new DiffFile(path, status, "@@ -0,0 +1 @@\n+x")]);

        Assert.Equal(DiffShapeFailure.FileNotModified, result.Failure);
        Assert.Equal(path, result.Detail);
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("uv.lock")]
    [InlineData("poetry.lock")]
    [InlineData("Cargo.lock")]
    [InlineData("charts/Chart.lock")]
    [InlineData("a/go.sum")]
    [InlineData(".terraform.lock.hcl")]
    [InlineData("packages.lock.json")]
    public void AModifiedLockfile_IsSkippedByName(string path)
    {
        var result = Classify([new DiffFile(path, "modified", "-x: 1\n+y: 2"), new DiffFile("v.yaml", "modified", null)]);

        Assert.Equal(DiffShapeFailure.MissingPatch, result.Failure);
        Assert.Equal("v.yaml", result.Detail);
        Assert.True(Classify([new DiffFile(path, "modified", "-x: 1\n+y: 2")]).Passed);
    }

    [Fact]
    public void AModifiedLockfileLookalike_IsJudgedLikeAnyFile()
    {
        var result = Classify([new DiffFile("go.sum.bak", "modified", Patch(("x: 1", "x: 2")))]);

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AFileWithoutAPatch_Fails(string? patch)
    {
        var result = Classify([new DiffFile("big.json", "modified", patch)]);

        Assert.Equal(DiffShapeFailure.MissingPatch, result.Failure);
    }

    [Fact]
    public void AListedCountThatDiffersFromTheFilesRead_Fails()
    {
        var result = Classify([new DiffFile("v.yaml", "modified", Patch(($"a: {ShaA}", $"a: {ShaB}")))], expectedCount: 2);

        Assert.Equal(DiffShapeFailure.FileCountMismatch, result.Failure);
    }

    [Fact]
    public void ALastCommitThatIsNotTheHeadTheFilesWereReadAt_Fails()
    {
        var files = new[] { new DiffFile("v.yaml", "modified", Patch(($"a: {ShaA}", $"a: {ShaB}"))) };
        var commits = new[] { new DiffCommit(Head, DefaultSubject), new DiffCommit("h2", DefaultSubject) };

        Assert.Equal(DiffShapeFailure.HeadCommitMismatch, Classify(files, commits: commits).Failure);
        Assert.Equal(DiffShapeFailure.HeadCommitMismatch, Classify(files, commits: []).Failure);
    }

    [Fact]
    public void AHeadThatMovedBetweenTheFileListAndTheReread_Fails()
    {
        var files = new[] { new DiffFile("v.yaml", "modified", Patch(($"a: {ShaA}", $"a: {ShaB}"))) };

        Assert.Equal(DiffShapeFailure.HeadMoved, Classify(files, headReread: "h2").Failure);
    }

    [Fact]
    public void TheHeadCommitsTable_IsTheOnlyOneRead()
    {
        var commits = new[]
        {
            new DiffCommit("h0", Message(DefaultSubject, "1.0.0>1.1.0")),
            new DiffCommit(Head, Message(DefaultSubject, "2.0.0>2.1.0")),
        };
        var files = new[] { new DiffFile("v.yaml", "modified", Patch(("a: 1.0.0", "a: 1.1.0"))) };

        Assert.Equal(DiffShapeFailure.VersionNotStated, Classify(files, commits: commits).Failure);
    }

    [Fact]
    public void AChangeWithNoVersionAtAll_Fails()
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line("name: foo", "name: bar").Failure);
    }

    [Fact]
    public void AMultiFilePr_PassesWhenEveryFilePasses()
    {
        var files = new[]
        {
            new DiffFile("a.yaml", "modified", Patch(($"x: {ShaA}", $"x: {ShaB}"))),
            new DiffFile("b.yaml", "modified", Patch(("v: 1.0.0", "v: 1.1.0"))),
            new DiffFile("go.sum", "modified", Patch(("h1:a", "h1:b"))),
        };

        Assert.True(Classify(files, Message(DefaultSubject, "1.0.0>1.1.0")).Passed);
    }

    private static string Hash(char c) => $"    --hash=sha256:{new string(c, 64)}";

    private static DiffShapeResult Requirements(string[] removed, string[] added, string rows = "8.0>8.1") =>
        Classify(
            [new DiffFile("docs/requirements.txt", "modified", "@@ -1,1 +1,1 @@\n" + string.Join("\n", removed.Select(l => "-" + l)) + "\n" + string.Join("\n", added.Select(l => "+" + l)))],
            Message(DefaultSubject, rows));

    [Fact]
    public void APipPinWithADifferentNumberOfHashLines_PassesWhenThePinIsStated()
    {
        var result = Requirements(
            ["backrefs==8.0 \\", Hash('1') + " \\", Hash('2')],
            ["backrefs==8.1 \\", Hash('3') + " \\", Hash('4') + " \\", Hash('5')]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void APipPinWithHashLines_StillNeedsItsVersionStated()
    {
        var result = Requirements(["backrefs==8.0 \\", Hash('1')], ["backrefs==8.2 \\", Hash('2'), Hash('3')]);

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void ANewPipPinWithNoRemoval_IsUnpaired()
    {
        var result = Classify([new DiffFile("requirements.txt", "modified", "@@ -1,1 +1,3 @@\n+evil==1.0 \\\n+" + Hash('1'))], Message(DefaultSubject, "8.0>8.1"));

        Assert.Equal(DiffShapeFailure.UnpairedLines, result.Failure);
    }

    [Theory]
    [InlineData("    # via mkdocs-material", "    # via evil")]
    [InlineData("-r base.txt", "-r evil.txt")]
    [InlineData("--index-url https://pypi.org/simple", "--index-url https://evil.example/simple")]
    public void AChangedPipOptionOrViaLine_BesideHashLines_IsNotAVersionChange(string oldLine, string newLine)
    {
        var result = Requirements(["backrefs==8.0 \\", Hash('1'), oldLine], ["backrefs==8.1 \\", Hash('2'), Hash('3'), newLine]);

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void ARunOfOnlyHashLines_IsNotAVersionChange()
    {
        var result = Requirements([Hash('1')], [Hash('2'), Hash('3')]);

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void ARunWithDroppedHashLines_NeedsAVersionPairNotJustADoubleEquals()
    {
        var result = Requirements(["foo==aaaaaaa \\", Hash('1')], ["foo==bbbbbbb \\", Hash('2'), Hash('3')]);

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void APackageRenamedBesideAVersionBump_IsNotAVersionChange()
    {
        var result = Requirements(["markdown==3.11 \\", Hash('1')], ["Markdown==3.11.1 \\", Hash('2')], "3.11>3.11.1");

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void AnImageRepositoryChange_BesideTheStatedTag_IsNotAVersionChange()
    {
        var result = Lines(
            null,
            "chore(deps): replace container image ghcr.io/unpoller/unpoller to v5.7.0",
            ("repository: ghcr.io/unpoller/unpoller", "repository: docker.io/golift/unifi-poller"));

        Assert.Equal(DiffShapeFailure.NotAVersionChange, result.Failure);
    }

    [Fact]
    public void APipPinWithCrlfLineEndings_PassesWhenThePinIsStated()
    {
        var result = Requirements(
            ["backrefs==8.0 \\\r", Hash('1') + " \\\r", Hash('2') + "\r"],
            ["backrefs==8.1 \\\r", Hash('3') + " \\\r", Hash('4') + " \\\r", Hash('5') + "\r"]);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("python-version: \"3.14\"", "python-version: \"3.15\"", "3.14.8>3.15.0")]
    [InlineData("version = \"~> 0.81\"", "version = \"~> 0.82\"", "0.81.0>0.82.0")]
    [InlineData("a: v3.14", "a: v3.15", "v3.14.8>v3.15.0")]
    public void AShortenedVersion_PassesWhenItPrefixesTheRowsFromAndTo(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows).Passed);
    }

    [Theory]
    [InlineData("a: 3.1", "a: 3.15")]
    [InlineData("a: 3.14", "a: 3.1")]
    [InlineData("a: 3.14", "a: 3.16")]
    [InlineData("a: 3.15", "a: 3.14")]
    [InlineData("a: 3.14-rc1", "a: 3.15-rc1")]
    public void AShortenedVersion_FailsWhenItIsNotADotBoundaryPrefixOfTheRow(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "3.14.8>3.15.0").Failure);
    }

    [Theory]
    [InlineData("requires = [\"uv_build>=0.12,<0.13\"]", "requires = [\"uv_build>=0.13,<0.14\"]", DiffShapeFailure.None)]
    [InlineData("requires = [\"uv_build>=0.12,<0.13\"]", "requires = [\"uv_build>=0.13,<0.15\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.12,<0.14\"]", "requires = [\"uv_build>=0.13,<0.15\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.11,<0.12\"]", "requires = [\"uv_build>=0.12,<0.13\"]", DiffShapeFailure.None)]
    [InlineData("requires = [\"uv_build>=0.12,<0.13\", \"x>=1.5\"]", "requires = [\"uv_build>=0.13,<0.14\", \"x>=1.6\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.12,<0.13\"]", "requires = [\"uv_build>=0.13,<0.14.0\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.12,<0.13,!=0.14\"]", "requires = [\"uv_build>=0.13,<0.14,!=0.15\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.12,<0.13,<0.13\"]", "requires = [\"uv_build>=0.13,<0.14,<0.14\"]", DiffShapeFailure.VersionNotStated)]
    [InlineData("requires = [\"uv_build>=0.12,<=0.13\"]", "requires = [\"uv_build>=0.13,<=0.14\"]", DiffShapeFailure.None)]
    public void ARangeUpperBound_FollowsOnlyTheStatedLowerBoundBumpedByOne(string oldLine, string newLine, DiffShapeFailure expected)
    {
        Assert.Equal(expected, Line(oldLine, newLine, "0.12.24>0.13.0").Failure);
    }

    [Fact]
    public void ARangeSuccessor_NeedsTheLessThanBeforeIt()
    {
        var result = Line("pins: foo==1.0 bar==1.1", "pins: foo==1.1 bar==1.2", "1.0>1.1");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Theory]
    [InlineData("node-version: \"24\"", "node-version: \"26\"")]
    [InlineData("python-version: \"v3\"", "python-version: \"v4\"")]
    public void AQuotedWholeNumber_PassesWhenTheRowStatesItsMajors(string oldLine, string newLine)
    {
        Assert.True(Line(oldLine, newLine, "24.22.1>26.12.0|3.14.8>4.0.0", datasource: "github-releases").Passed);
    }

    [Theory]
    [InlineData("node-version: \"24\"", "node-version: \"25\"")]
    [InlineData("node-version: \"23\"", "node-version: \"26\"")]
    [InlineData("node-version: \"26\"", "node-version: \"24\"")]
    public void AQuotedWholeNumber_FailsWhenItIsNotTheStatedMajors(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "24.22.1>26.12.0", datasource: "github-releases").Failure);
    }

    [Theory]
    [InlineData("node-version: \"24.x\"", "node-version: \"26.x\"")]
    [InlineData("node-version: \"x24\"", "node-version: \"x26\"")]
    [InlineData("node-version: \"24 \"", "node-version: \"26 \"")]
    [InlineData("node-version: 24", "node-version: 26")]
    public void ANumberThatIsNotAWholeQuotedValue_IsNotAVersionToken(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.NotAVersionChange, Line(oldLine, newLine, "24.22.1>26.12.0", datasource: "github-releases").Failure);
    }

    [Fact]
    public void AQuotedWholeNumber_FollowsTheSubjectVersionLikeAnyOtherSinglePartToken()
    {
        Assert.True(Line("node-version: \"24\"", "node-version: \"26\"", subject: "chore(deps): update dependency node to v26").Passed);
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("node-version: \"24\"", "node-version: \"26\"", subject: "chore(deps): update dependency node to v27").Failure);
    }

    [Theory]
    [InlineData("pkg==2.9.0.post0 \\", "pkg==3.0.0 \\", "2.9.0.post0>3.0.0")]
    [InlineData("pkg==2.9.0.dev3 \\", "pkg==2.9.0 \\", "2.9.0.dev3>2.9.0")]
    [InlineData("pkg==1.0a1 \\", "pkg==1.0 \\", "1.0a1>1.0")]
    [InlineData("pkg==1.0.0b2 \\", "pkg==1.0.0 \\", "1.0.0b2>1.0.0")]
    [InlineData("pkg==1.0rc1 \\", "pkg==1.0 \\", "1.0rc1>1.0")]
    [InlineData("pkg==1.0.0 \\", "pkg==1.1.0rc1 \\", "1.0.0>1.1.0rc1")]
    public void APep440Suffix_IsPartOfTheVersion(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows, datasource: "pypi").Passed);
    }

    [Fact]
    public void APep440Suffix_MustBeStatedByTheRow()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("pkg==2.9.0.post0 \\", "pkg==3.0.0 \\", "2.9.0>3.0.0", datasource: "pypi").Failure);
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("pkg==2.9.0.post0 \\", "pkg==3.0.0 \\", "2.9.0.post1>3.0.0", datasource: "pypi").Failure);
    }

    [Theory]
    [InlineData("ARG PYTHON_VERSION=3.14-slim@{1}", "ARG PYTHON_VERSION=3.15-slim@{2}", "3.14>3.15")]
    [InlineData("tag: \"1.8.0-wine@{1}\"", "tag: \"1.8.1-wine@{2}\"", "1.8.0>1.8.1")]
    [InlineData("ARG V=v3.14.8-slim", "ARG V=v3.15.0-slim", "3.14.8>3.15.0")]
    public void ASuffixedVersionOutsideAnImageReference_PassesAgainstADockerRow(string oldLine, string newLine, string rows)
    {
        var result = Line(
            oldLine.Replace("{1}", _digestOne, StringComparison.Ordinal),
            newLine.Replace("{2}", _digestTwo, StringComparison.Ordinal),
            rows);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("3.14-slim", "3.15-alpine")]
    [InlineData("3.14-slim", "3.15")]
    [InlineData("3.14", "3.15-slim")]
    [InlineData("3.14-slim", "3.16-slim")]
    [InlineData("3.13-slim", "3.15-slim")]
    [InlineData("3.14-slim", "3.15-slim-bookworm")]
    public void ASuffixedVersionOutsideAnImageReference_FailsWhenTheSuffixOrNumberDoesNotMatchTheRow(string oldTag, string newTag)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line($"ARG V={oldTag}", $"ARG V={newTag}", "3.14>3.15").Failure);
    }

    [Theory]
    [InlineData("github-releases")]
    [InlineData("github-tags")]
    [InlineData("pypi")]
    [InlineData("npm")]
    [InlineData("terraform-provider")]
    public void ASuffixedVersionOutsideAnImageReference_FailsAgainstARowThatIsNotDocker(string datasource)
    {
        var result = Line("ARG PYTHON_VERSION=3.14-slim", "ARG PYTHON_VERSION=3.15-slim", "3.14>3.15", datasource: datasource);

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Theory]
    [InlineData("- https://h/autoscaler/vertical-pod-autoscaler-1.8.0/crd.yaml", "- https://h/autoscaler/vertical-pod-autoscaler-1.8.1/crd.yaml", "vertical-pod-autoscaler-1.8.0>vertical-pod-autoscaler-1.8.1")]
    [InlineData("ref: release-v1.2.3", "ref: release-v1.2.4", "release-v1.2.3>release-v1.2.4")]
    [InlineData("ref: release-1.2", "ref: release-1.3", "release-1.2.3>release-1.3.0")]
    [InlineData("ref: a-b-1.2.3", "ref: a-b-1.2.4", "a-b-1.2.3>a-b-1.2.4")]
    public void AVersionWithANamePrefix_PassesWhenTheRowStatesTheSamePrefixBeforeItsNumbers(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows, datasource: "github-tags").Passed);
    }

    [Theory]
    [InlineData("url: https://h/autoscaler/1.8.0/crd.yaml", "url: https://h/autoscaler/1.8.1/crd.yaml")]
    [InlineData("url: https://h/other-1.8.0/crd.yaml", "url: https://h/other-1.8.1/crd.yaml")]
    [InlineData("url: https://h/vertical-pod-autoscaler-x/1.8.0", "url: https://h/vertical-pod-autoscaler-x/1.8.1")]
    [InlineData("url: https://h/vertical-pod-autoscaler-1.9.0/crd.yaml", "url: https://h/vertical-pod-autoscaler-1.9.1/crd.yaml")]
    [InlineData("url: https://h/vertical-pod-autoscaler-1.8.0/crd.yaml", "url: https://h/vertical-pod-autoscaler-1.8.2/crd.yaml")]
    [InlineData("url: https://h/vertical-pod-autoscaler-1.7.0/crd.yaml", "url: https://h/vertical-pod-autoscaler-1.8.1/crd.yaml")]
    public void AVersionWithANamePrefix_FailsWhenThePrefixIsNotRightBeforeTheMatchingNumbers(string oldLine, string newLine)
    {
        var result = Line(oldLine, newLine, "vertical-pod-autoscaler-1.8.0>vertical-pod-autoscaler-1.8.1", datasource: "github-tags");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Theory]
    [InlineData("vertical-pod-autoscaler-1.8.0>autoscaler-1.8.1")]
    [InlineData("vertical-pod-autoscaler-1.8.0>1.8.1")]
    [InlineData("1.8.0>vertical-pod-autoscaler-1.8.1")]
    [InlineData("vertical-pod-autoscaler/1.8.0>vertical-pod-autoscaler/1.8.1")]
    [InlineData("vertical-pod-autoscaler-next>vertical-pod-autoscaler-nexus")]
    public void AVersionWithANamePrefix_FailsWhenTheRowsDoNotShareAPrefixEndingInADashBeforeAVersion(string rows)
    {
        var result = Line("ref: x/vertical-pod-autoscaler-1.8.0", "ref: x/vertical-pod-autoscaler-1.8.1", rows, datasource: "github-tags");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void AVersionAfterAPathSeparator_IsNotAPrefixedVersion()
    {
        var result = Line("ref: x/releases/1.8.0", "ref: x/releases/1.8.1", "releases/1.8.0>releases/1.8.1", datasource: "github-tags");

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Theory]
    [InlineData("\"@types/node\": \"^24.12.2\"", "\"@types/node\": \"^26.0.0\"", "24.19.2>26.6.5")]
    [InlineData("version = \"~> 2.38\"", "version = \"~> 3.0\"", "2.38.0>3.4.0")]
    [InlineData("version = \"~>2.38\"", "version = \"~>3.0\"", "2.38.0>3.4.0")]
    [InlineData("\"a\": \"~24.12.2\"", "\"a\": \"~26.0.0\"", "24.19.2>26.6.5")]
    [InlineData("requires = \">=24.12\"", "requires = \">=26.0\"", "24.19.2>26.6.5")]
    [InlineData("\"a\": \"^24.9.0\"", "\"a\": \"^26.0.0\"", "24.10.0>26.6.5")]
    [InlineData("\"a\": \"^24.19.2\"", "\"a\": \"^26.6.5\"", "24.19.2>26.6.5")]
    [InlineData("\"a\": \"^v24.12.2\"", "\"a\": \"^v26.0.0\"", "v24.19.2>v26.6.5")]
    public void ARangeLowerBound_PassesWhenItStaysInTheStatedMajorsAndNotAboveTheStatedVersions(string oldLine, string newLine, string rows)
    {
        Assert.True(Line(oldLine, newLine, rows, datasource: "npm").Passed);
    }

    [Theory]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^26.7.0\"")]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^26.6.6\"")]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^26.6.5.1\"")]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^25.0.0\"")]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^27.0.0\"")]
    [InlineData("\"a\": \"^24.20.0\"", "\"a\": \"^26.0.0\"")]
    [InlineData("\"a\": \"^24.19.2.1\"", "\"a\": \"^26.0.0\"")]
    [InlineData("\"a\": \"^23.0.0\"", "\"a\": \"^26.0.0\"")]
    [InlineData("\"a\": \"^25.0.0\"", "\"a\": \"^26.0.0\"")]
    [InlineData("\"a\": \"^24.12.2-rc.1\"", "\"a\": \"^26.0.0\"")]
    [InlineData("\"a\": \"^24.12.2\"", "\"a\": \"^26.0.0-rc.1\"")]
    [InlineData("\"a\": \"^26.0.0\"", "\"a\": \"^24.12.2\"")]
    public void ARangeLowerBound_FailsWhenABoundIsAboveTheStatedVersionOrInAnotherMajor(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "24.19.2>26.6.5", datasource: "npm").Failure);
    }

    [Theory]
    [InlineData("\"a\": \"24.12.2\"", "\"a\": \"26.0.0\"")]
    [InlineData("\"a\": \"==24.12.2\"", "\"a\": \"==26.0.0\"")]
    [InlineData("\"a\": \"<24.12.2\"", "\"a\": \"<26.0.0\"")]
    [InlineData("\"a\": \"<=24.12.2\"", "\"a\": \"<=26.0.0\"")]
    [InlineData("\"a\": \">24.12.2\"", "\"a\": \">26.0.0\"")]
    [InlineData("\"a\": \"!=24.12.2\"", "\"a\": \"!=26.0.0\"")]
    [InlineData("\"a\": \"^ 24.12.2\"", "\"a\": \"^ 26.0.0\"")]
    [InlineData("\"a\": \"~ 24.12.2\"", "\"a\": \"~ 26.0.0\"")]
    [InlineData("\"a\": \"~/24.12.2\"", "\"a\": \"~/26.0.0\"")]
    public void ARangeLowerBound_NeedsTheOperatorRightBeforeTheNumber(string oldLine, string newLine)
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line(oldLine, newLine, "24.19.2>26.6.5", datasource: "npm").Failure);
    }

    [Fact]
    public void ABareShortenedVersionThatIsNotAPrefixOfTheRow_FailsWithoutARangeOperator()
    {
        Assert.Equal(DiffShapeFailure.VersionNotStated, Line("version = \"2.38\"", "version = \"3.0\"", "2.38.0>3.4.0", datasource: "terraform-provider").Failure);
    }

    private static DiffShapeResult GoMod(string rows, string datasource, params (string Old, string New)[] lines) =>
        Classify([new DiffFile("go.mod", "modified", Patch(lines))], Message(DefaultSubject, rows, datasource));

    private static readonly (string Old, string New) _directBump = ("\tgithub.com/p/c v1.24.1", "\tgithub.com/p/c v1.25.0");

    [Fact]
    public void AnIndirectGoRequireThatGoesUp_PassesBesideAStatedGoRow()
    {
        var result = GoMod("v1.24.1>v1.25.0", "go", _directBump, ("\tgithub.com/p/m v0.70.1 // indirect", "\tgithub.com/p/m v0.72.0 // indirect"));

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("\tgithub.com/p/m v0.72.0 // indirect", "\tgithub.com/p/m v0.70.1 // indirect")]
    [InlineData("\tgithub.com/p/m v0.70.1", "\tgithub.com/p/m v0.72.0")]
    [InlineData("\tgithub.com/p/m v0.70.1 // direct", "\tgithub.com/p/m v0.72.0 // direct")]
    [InlineData("\tgithub.com/p/m v0.70.1 // indirect", "\tgithub.com/p/m v0.70.1-rc1 // indirect")]
    [InlineData("replace github.com/p/m => github.com/q/m v0.70.1 // indirect", "replace github.com/p/m => github.com/q/m v0.72.0 // indirect")]
    [InlineData("exclude github.com/p/m v0.70.1 // indirect", "exclude github.com/p/m v0.72.0 // indirect")]
    [InlineData("go 1.27.1 // indirect", "go 1.27.2 // indirect")]
    [InlineData("\tgithub.com/evil/lib-1.2 v1.0.0 // indirect", "\tgithub.com/evil/lib-1.3 v1.0.1 // indirect")]
    [InlineData("\tretract v0.70.1 // indirect", "\tretract v0.72.0 // indirect")]
    [InlineData("toolchain go1.27.1 // indirect", "toolchain go1.27.2 // indirect")]
    public void AnIndirectGoRequireThatDoesNotGoUp_OrIsNotARequireLine_Fails(string oldLine, string newLine)
    {
        var result = GoMod("v1.24.1>v1.25.0", "go", _directBump, (oldLine, newLine));

        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("go")]
    [InlineData("docker")]
    public void AnIndirectGoRequire_NeedsAStatedGoRowForAPairInTheSameGoMod(string datasource)
    {
        var indirect = ("\tgithub.com/p/m v0.70.1 // indirect", "\tgithub.com/p/m v0.72.0 // indirect");
        var anchorElsewhere = Classify(
            [
                new DiffFile("tools/go.mod", "modified", Patch(_directBump)),
                new DiffFile("go.mod", "modified", Patch(indirect)),
            ],
            Message(DefaultSubject, "v1.24.1>v1.25.0", datasource));
        var onlyIndirect = GoMod("v1.24.1>v1.25.0", datasource, indirect);

        Assert.Equal(DiffShapeFailure.VersionNotStated, anchorElsewhere.Failure);
        Assert.Equal(DiffShapeFailure.VersionNotStated, onlyIndirect.Failure);
    }

    [Fact]
    public void AnIndirectGoRequire_FailsWhenTheCommitStatesNoTable()
    {
        var result = Classify([new DiffFile("go.mod", "modified", Patch(("\tgithub.com/p/m v0.70.1 // indirect", "\tgithub.com/p/m v0.72.0 // indirect")))], DefaultSubject);

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void AnIndirectLineOutsideGoMod_IsNotRelaxed()
    {
        var result = Classify(
            [
                new DiffFile("go.mod", "modified", Patch(_directBump)),
                new DiffFile("notes.txt", "modified", Patch(("\tgithub.com/p/m v0.70.1 // indirect", "\tgithub.com/p/m v0.72.0 // indirect"))),
            ],
            Message(DefaultSubject, "v1.24.1>v1.25.0", "go"));

        Assert.Equal(DiffShapeFailure.VersionNotStated, result.Failure);
    }

    [Fact]
    public void AnIndirectGoRequireWithCrlfLineEndings_PassesBesideAStatedGoRow()
    {
        var result = GoMod("v1.24.1>v1.25.0", "go", (_directBump.Old + "\r", _directBump.New + "\r"), ("\tgithub.com/p/m v0.70.1 // indirect\r", "\tgithub.com/p/m v0.72.0 // indirect\r"));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ANewGoRequireLine_IsUnpaired()
    {
        var patch = "@@ -1,1 +1,2 @@\n-\tgithub.com/p/c v1.24.1\n+\tgithub.com/p/c v1.25.0\n+\tgithub.com/p/m v0.72.0 // indirect";
        var result = Classify([new DiffFile("go.mod", "modified", patch)], Message(DefaultSubject, "v1.24.1>v1.25.0", "go"));

        Assert.Equal(DiffShapeFailure.UnpairedLines, result.Failure);
    }
}
