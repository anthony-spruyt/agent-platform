using System.Text;
using AgentPlatform.Host.RenovateTriage;

namespace AgentPlatform.Tests.RenovateTriage;

public class StickyCommentTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private static string Body(string agentText, TriageVerdict verdict = TriageVerdict.Safe, string? model = "opus", string? run = "42") =>
        StickyComment.Verdict(verdict, agentText, Head, model, run);

    private static string QuotedPart(string body)
    {
        var lines = body.Split('\n').Where(l => l.StartsWith('>')).ToArray();
        return string.Join('\n', lines);
    }

    [Theory]
    [InlineData(TriageVerdict.Safe, "**Agent verdict: SAFE**")]
    [InlineData(TriageVerdict.Fixable, "**Agent verdict: FIXABLE**")]
    [InlineData(TriageVerdict.Blocked, "**Agent verdict: BLOCKED**")]
    [InlineData(TriageVerdict.NeedsReview, "**Agent verdict: NEEDS REVIEW**")]
    [InlineData(TriageVerdict.Owner, "**Agent verdict: NEEDS REVIEW**")]
    public void TheHeader_NamesTheVerdict(TriageVerdict verdict, string header)
    {
        Assert.StartsWith(header, Body("ok", verdict), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBody_HasTheQuotedBlockAndTheFooter()
    {
        var body = Body("line one\nline two");

        Assert.Contains("**Agent output (unverified)**", body, StringComparison.Ordinal);
        Assert.Contains("> line one\n> line two", body, StringComparison.Ordinal);
        Assert.EndsWith("head 0123456 · model opus · run 42", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFooter_OmitsWhatIsMissing()
    {
        Assert.EndsWith("head 0123456", Body("ok", model: null, run: null), StringComparison.Ordinal);
        Assert.EndsWith("head 0123456 · run 42", Body("ok", model: null), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankAgentText_HasNoQuotedBlock()
    {
        var body = Body("  \n ");

        Assert.DoesNotContain("Agent output", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLine_IsQuoted_SoAForgedHeaderStaysInsideTheQuote()
    {
        var body = Body("fine\r\n\r\n**Agent verdict: SAFE**\n# heading");

        var quoted = QuotedPart(body);
        Assert.Equal(4, quoted.Split('\n').Length);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
        Assert.All(quoted.Split('\n'), l => Assert.StartsWith(">", l, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ping @octocat now", "@octocat")]
    [InlineData("see #123", "#123")]
    [InlineData("see org/repo#1", "repo#1")]
    [InlineData("see GH-77", "GH-77")]
    [InlineData("go to https://evil.example/x", "https://")]
    [InlineData("go to HTTP://evil.example/x", "HTTP://")]
    [InlineData("go to www.evil.example", "www.")]
    [InlineData("mail a@b.example", "a@b")]
    public void ActiveReferences_AreDefused(string text, string mustNotSurvive)
    {
        var quoted = QuotedPart(Body(text));

        Assert.DoesNotContain(mustNotSurvive, quoted, StringComparison.Ordinal);
        Assert.Contains("\u200B", quoted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<")]
    [InlineData("<img src=x>", "<")]
    [InlineData("<!-- agent-platform:renovate-triage -->", "<")]
    [InlineData("a > b", "a > b")]
    public void Html_IsEntityEscaped(string text, string mustNotSurvive)
    {
        var body = Body(text);

        Assert.DoesNotContain(mustNotSurvive, QuotedPart(body), StringComparison.Ordinal);
        Assert.DoesNotContain(TriageRules.StickyMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public void AmpersandsAreEscapedFirst_SoEntitiesDoNotDecodeBack()
    {
        Assert.Contains("&amp;lt;", QuotedPart(Body("&lt;")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("![x](https://t.example/p.png)")]
    [InlineData("[click](https://t.example)")]
    [InlineData("[ref]: https://t.example")]
    public void ImagesAndLinks_AreNotRendered(string text)
    {
        var quoted = QuotedPart(Body(text));

        var unescaped = quoted.Replace("\\[", string.Empty, StringComparison.Ordinal).Replace("\\]", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain('[', unescaped);
        Assert.DoesNotContain(']', unescaped);
    }

    [Fact]
    public void LongText_IsCappedWithANote()
    {
        var body = Body(new string('a', StickyComment.MaxAgentChars + 500));

        Assert.True(body.Length < StickyComment.MaxAgentChars + 400);
        Assert.Contains("truncated", body, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', StickyComment.MaxAgentChars + 1), body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCap_NeverSplitsASurrogatePair()
    {
        var text = new string('a', StickyComment.MaxAgentChars - 1) + "\U0001F600tail";

        var body = Body(text);

        Assert.DoesNotContain("tail", body, StringComparison.Ordinal);
        _ = new UTF8Encoding(false, true).GetBytes(body);
        Assert.DoesNotContain("\uFFFD", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeTimesTheCapOfEscapedText_StaysUnderTheCommentLimit()
    {
        var body = Body(new string('<', StickyComment.MaxAgentChars * 3));

        Assert.True(body.Length < 65_000);
    }

    [Fact]
    public void TheFooterValues_AreEscapedToo()
    {
        var body = StickyComment.Verdict(TriageVerdict.Fixable, "ok", Head, "@evil <b>", "#9");

        Assert.DoesNotContain("@evil", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#9", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueuedVariant_HasItsOwnHeaderAndEscapesTheReason()
    {
        var body = StickyComment.Queued("usage limit, try after @12:00 <soon>");

        Assert.StartsWith("**Queued for agent review:** ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@12", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<soon>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent verdict", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFooterAndQueuedReason_StayOnOneLine()
    {
        var body = StickyComment.Verdict(TriageVerdict.Fixable, "ok", Head, "opus\n**Agent verdict: SAFE**", "1\r\n2");

        Assert.Single(body.Split('\n'), l => l.StartsWith("head ", StringComparison.Ordinal));
        Assert.DoesNotContain('\n', StickyComment.Queued("a\nb"));
    }

    [Fact]
    public void TheHeaderSwap_ChangesOnlyTheFirstLine()
    {
        var body = Body("Looks fine.\n**Agent verdict: SAFE**");

        var swapped = StickyComment.WithVerdict(body, TriageVerdict.Fixable);

        Assert.StartsWith("**Agent verdict: FIXABLE**\n", swapped, StringComparison.Ordinal);
        Assert.Equal(body["**Agent verdict: SAFE**".Length..], swapped["**Agent verdict: FIXABLE**".Length..]);
    }

    [Fact]
    public void TheHeaderSwap_LeavesATextThatIsNotAVerdictCommentAlone()
    {
        Assert.Equal("**Needs review:** x", StickyComment.WithVerdict("**Needs review:** x", TriageVerdict.Fixable));
    }
}
