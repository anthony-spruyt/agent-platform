using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.PullRequests;

namespace AgentPlatform.Tests.Agents;

public class TriageResultTests
{
    private static readonly string Sha = new('a', 40);

    private static SubmitResultPayload Payload(string data, string status = "success", string summary = "Looks fine.") =>
        new(status, summary, JsonDocument.Parse(data).RootElement.Clone());

    private static string Valid(string verdict = "SAFE", string? sha = null) =>
        $$"""{"verdict":"{{verdict}}","head_sha":"{{sha ?? Sha}}","versions":[{"dep":"actions/checkout","from":"4.1.0","to":"4.2.0"}],"fix":[],"reasons":["CI green"]}""";

    private static string? Error(SubmitResultPayload payload)
    {
        TriageResult.TryParse(payload, Sha, null, out _, out var error);
        return error;
    }

    [Theory]
    [InlineData("SAFE")]
    [InlineData("FIXABLE")]
    [InlineData("BLOCKED")]
    [InlineData("NEEDS_REVIEW")]
    public void TryParse_AcceptsEachVerdict(string verdict)
    {
        Assert.True(TriageResult.TryParse(Payload(Valid(verdict)), Sha, null, out var result, out var error), error);

        Assert.Equal(verdict, result!.Verdict);
        Assert.Equal("Looks fine.", result.Summary);
        Assert.Equal(new TriageVersion("actions/checkout", "4.1.0", "4.2.0"), Assert.Single(result.Versions));
        Assert.Equal(["CI green"], result.Reasons);
        Assert.Empty(result.Fix);
    }

    [Fact]
    public void TryParse_AcceptsTheHeadShaInAnyCase()
    {
        Assert.Null(Error(Payload(Valid(sha: Sha.ToUpperInvariant()))));
    }

    [Fact]
    public void TryParse_TreatsMissingListsAsEmpty()
    {
        Assert.True(TriageResult.TryParse(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}"}"""), Sha, null, out var result, out _));
        Assert.Empty(result!.Versions);
        Assert.Empty(result.Fix);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void TryParse_RejectsAFailureStatus()
    {
        Assert.Contains("success", Error(Payload(Valid(), status: "failure")), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsMissingData()
    {
        Assert.Contains("data", Error(new SubmitResultPayload("success", "x", null)), StringComparison.Ordinal);
        Assert.Contains("data", Error(Payload("[]")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"head_sha":"AAAA"}""")]
    [InlineData("""{"verdict":"MAYBE","head_sha":"AAAA"}""")]
    [InlineData("""{"verdict":"safe","head_sha":"AAAA"}""")]
    [InlineData("""{"verdict":5,"head_sha":"AAAA"}""")]
    public void TryParse_RejectsAnUnknownVerdict(string data)
    {
        Assert.Contains("verdict", Error(Payload(data.Replace("AAAA", Sha, StringComparison.Ordinal))), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsAShaThatIsNotTheRunsHead()
    {
        Assert.Contains("head_sha", Error(Payload(Valid(sha: new string('b', 40)))), StringComparison.Ordinal);
        Assert.Contains("head_sha", Error(Payload($$"""{"verdict":"SAFE"}""")), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_CapsTheSummary()
    {
        Assert.Contains("summary", Error(Payload(Valid(), summary: new string('x', TriageResult.MaxSummaryLength + 1))), StringComparison.Ordinal);
        Assert.Contains("summary", Error(Payload(Valid(), summary: " ")), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_CapsTheVersionList()
    {
        var versions = string.Join(',', Enumerable.Repeat("""{"dep":"d","from":"1","to":"2"}""", TriageResult.MaxVersions + 1));

        Assert.Contains("versions", Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","versions":[{{versions}}]}""")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"dep":"","from":"1","to":"2"}""")]
    [InlineData("""{"dep":"d","from":"1"}""")]
    [InlineData("""{"dep":"d","from":1,"to":"2"}""")]
    [InlineData("\"d\"")]
    public void TryParse_RejectsAMalformedVersionEntry(string entry)
    {
        Assert.Contains("versions", Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","versions":[{{entry}}]}""")), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_CapsEachVersionField()
    {
        var long_ = new string('x', TriageResult.MaxVersionLength + 1);

        Assert.Contains("versions", Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","versions":[{"dep":"d","from":"{{long_}}","to":"2"}]}""")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fix")]
    [InlineData("reasons")]
    public void TryParse_CapsTheStringLists(string field)
    {
        var tooMany = string.Join(',', Enumerable.Repeat("\"x\"", TriageResult.MaxListItems + 1));
        var tooLong = $"\"{new string('x', TriageResult.MaxItemLength + 1)}\"";

        Assert.Contains(field, Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","{{field}}":[{{tooMany}}]}""")), StringComparison.Ordinal);
        Assert.Contains(field, Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","{{field}}":[{{tooLong}}]}""")), StringComparison.Ordinal);
        Assert.Contains(field, Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","{{field}}":"nope"}""")), StringComparison.Ordinal);
        Assert.Contains(field, Error(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","{{field}}":[1]}""")), StringComparison.Ordinal);
    }

    private static StatedVersions Table(params (string Dep, string From, string To)[] rows) =>
        new([.. rows.Select(r => new StatedVersion(r.Dep, r.From, r.To))], null);

    private static string? VersionsError(string versions, StatedVersions stated)
    {
        var payload = Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}","versions":{{versions}}}""");
        TriageResult.TryParse(payload, Sha, stated, out _, out var error);
        return error;
    }

    [Fact]
    public void TryParse_AcceptsVersionsEqualToTheCommitTable_InAnyOrderIgnoringALeadingV()
    {
        var stated = Table(("a", "1.0.0", "1.1.0"), ("b", "v2.0.0", "v2.1.0"));

        Assert.Null(VersionsError("""[{"dep":"b","from":"2.0.0","to":"2.1.0"},{"dep":"a","from":"v1.0.0","to":"1.1.0"}]""", stated));
    }

    [Theory]
    [InlineData("""[{"dep":"a","from":"1.0.0","to":"1.1.0"}]""")]
    [InlineData("""[{"dep":"a","from":"1.0.0","to":"1.1.0"},{"dep":"b","from":"2.0.0","to":"2.1.0"},{"dep":"c","from":"3.0.0","to":"3.1.0"}]""")]
    [InlineData("""[{"dep":"a","from":"1.0.0","to":"1.1.0"},{"dep":"b","from":"2.0.0","to":"2.2.0"}]""")]
    [InlineData("""[{"dep":"a","from":"1.0.0","to":"1.1.0"},{"dep":"x","from":"2.0.0","to":"2.1.0"}]""")]
    [InlineData("""[{"dep":"a","from":"1.0.0","to":"1.1.0"},{"dep":"a","from":"1.0.0","to":"1.1.0"}]""")]
    [InlineData("[]")]
    public void TryParse_RejectsVersionsThatDifferFromTheCommitTable(string versions)
    {
        var stated = Table(("a", "1.0.0", "1.1.0"), ("b", "2.0.0", "2.1.0"));

        Assert.Contains("data.versions", VersionsError(versions, stated), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_DoesNotRequireALockfileOnlyPairTheTableDoesNotList()
    {
        var stated = Table(("a", "1.0.0", "1.1.0"));

        Assert.Null(VersionsError("""[{"dep":"a","from":"1.0.0","to":"1.1.0"}]""", stated));
    }

    [Theory]
    [InlineData("[]", "8.22.0")]
    [InlineData("[]", null)]
    [InlineData("""[{"dep":"x","from":"8.21.0","to":"8.22.0"}]""", "8.22.0")]
    [InlineData("""[{"dep":"x","from":"8.21.0","to":"v8.22.0"}]""", "8.22.0")]
    [InlineData("""[{"dep":"x","from":"8.21.0","to":"8.22.0"}]""", "v8.22.0")]
    public void TryParse_WithNoTable_AcceptsNoVersionsOrOneEntryEndingAtTheSubjectVersion(string versions, string? subjectVersion)
    {
        Assert.Null(VersionsError(versions, new StatedVersions(null, subjectVersion)));
    }

    [Theory]
    [InlineData("""[{"dep":"x","from":"8.21.0","to":"8.23.0"}]""", "8.22.0")]
    [InlineData("""[{"dep":"x","from":"8.21.0","to":"8.22.0"}]""", null)]
    [InlineData("""[{"dep":"x","from":"1","to":"8.22.0"},{"dep":"y","from":"1","to":"8.22.0"}]""", "8.22.0")]
    public void TryParse_WithNoTable_RejectsAnythingElse(string versions, string? subjectVersion)
    {
        Assert.Contains("data.versions", VersionsError(versions, new StatedVersions(null, subjectVersion)), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_TellsTheAgentWhichUpdatesTheCommitStates()
    {
        var error = VersionsError("[]", Table(("a", "1.0.0", "1.1.0")));

        Assert.Contains("a 1.0.0 -> 1.1.0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ChecksTheVersionsOnlyAfterTheShapeIsValid()
    {
        var payload = Payload($$"""{"verdict":"MAYBE","head_sha":"{{Sha}}"}""");

        TriageResult.TryParse(payload, Sha, Table(("a", "1", "2")), out _, out var error);

        Assert.Contains("verdict", error, StringComparison.Ordinal);
    }
}
