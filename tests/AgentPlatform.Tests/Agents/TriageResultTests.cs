using System.Text.Json;
using AgentPlatform.Host.Agents;

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
        TriageResult.TryParse(payload, Sha, out _, out var error);
        return error;
    }

    [Theory]
    [InlineData("SAFE")]
    [InlineData("FIXABLE")]
    [InlineData("BLOCKED")]
    [InlineData("NEEDS_REVIEW")]
    public void TryParse_AcceptsEachVerdict(string verdict)
    {
        Assert.True(TriageResult.TryParse(Payload(Valid(verdict)), Sha, out var result, out var error), error);

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
        Assert.True(TriageResult.TryParse(Payload($$"""{"verdict":"SAFE","head_sha":"{{Sha}}"}"""), Sha, out var result, out _));
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
}
