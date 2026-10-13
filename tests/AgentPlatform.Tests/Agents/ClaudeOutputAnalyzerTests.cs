using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class ClaudeOutputAnalyzerTests
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"s-1","model":"sonnet"}""";
    private const string Assistant = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Checking the rate limit config now"}],"stop_reason":null}}""";

    private static string Result(
        string subtype = "success",
        bool isError = false,
        string result = "done",
        string stopReason = "\"end_turn\"",
        int outputTokens = 120) =>
        $$$"""{"type":"result","subtype":"{{{subtype}}}","is_error":{{{(isError ? "true" : "false")}}},"duration_ms":4200,"num_turns":3,"result":"{{{result}}}","stop_reason":{{{stopReason}}},"session_id":"s-1","total_cost_usd":0.0123,"usage":{"input_tokens":900,"output_tokens":{{{outputTokens}}}}}""";

    private static Task<ClaudeOutputSummary> Analyze(params string[] lines) =>
        ClaudeOutputAnalyzer.AnalyzeAsync(new StringReader(string.Join('\n', lines)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task CleanRun_IsNotTruncatedAndExposesUsage()
    {
        var summary = await Analyze(Init, Assistant, Result());

        Assert.False(summary.IsTruncated);
        Assert.False(summary.UsageLimitReached);
        Assert.Equal("done", summary.ResultText);
        Assert.Equal(new AgentRunUsage(900, 120, 0.0123, 4200, 3, "s-1"), summary.Usage);
    }

    [Fact]
    public async Task EmptyStdout_IsTruncated()
    {
        var summary = await Analyze();

        Assert.True(summary.IsTruncated);
        Assert.False(summary.HasResultEvent);
    }

    [Fact]
    public async Task MissingResultEvent_IsTruncated()
    {
        var summary = await Analyze(Init, Assistant);

        Assert.True(summary.IsTruncated);
    }

    [Theory]
    [InlineData("error_max_turns")]
    [InlineData("error_during_execution")]
    public async Task NonSuccessSubtype_IsTruncated(string subtype)
    {
        var summary = await Analyze(Init, Result(subtype: subtype));

        Assert.True(summary.IsTruncated);
    }

    [Fact]
    public async Task EmptyResultText_IsTruncated()
    {
        var summary = await Analyze(Init, Result(result: ""));

        Assert.True(summary.IsTruncated);
    }

    [Fact]
    public async Task ZeroOutputTokens_IsTruncated()
    {
        var summary = await Analyze(Init, Result(outputTokens: 0));

        Assert.True(summary.IsTruncated);
    }

    [Fact]
    public async Task NullStopReason_WithResultText_IsNotTruncated()
    {
        var summary = await Analyze(Init, Result(stopReason: "null"));

        Assert.False(summary.IsTruncated);
        Assert.True(summary.StopReasonIsNull);
    }

    [Fact]
    public async Task NullStopReason_WithEmptyResultText_IsTruncated()
    {
        var summary = await Analyze(Init, Result(result: "", stopReason: "null"));

        Assert.True(summary.IsTruncated);
    }

    [Fact]
    public async Task AbsentStopReason_IsNotTruncated()
    {
        const string noStopReason = """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":1,"output_tokens":5}}""";

        var summary = await Analyze(Init, noStopReason);

        Assert.False(summary.IsTruncated);
    }

    [Fact]
    public async Task LastResultEventWins()
    {
        var summary = await Analyze(Result(result: ""), Result(result: "second"));

        Assert.False(summary.IsTruncated);
        Assert.Equal("second", summary.ResultText);
    }

    [Fact]
    public async Task NonJsonAndBlankLines_AreIgnored()
    {
        var summary = await Analyze("warning: something on stderr", "", "{not json", Init, Result());

        Assert.False(summary.IsTruncated);
    }

    [Theory]
    [InlineData("Claude AI usage limit reached|1760000000")]
    [InlineData("5-hour limit reached ∙ resets 3am")]
    [InlineData("You've hit your limit · resets 2pm (UTC)")]
    [InlineData("Weekly limit reached ∙ resets Oct 20")]
    public async Task UsageLimitText_InAnErrorResult_IsDetected(string message)
    {
        var summary = await Analyze(Init, Result(subtype: "success", isError: true, result: message, outputTokens: 0));

        Assert.True(summary.UsageLimitReached);
        Assert.Equal(message, summary.UsageLimitMessage);
        Assert.True(summary.IsTruncated);
    }

    [Fact]
    public async Task RejectedRateLimitEvent_IsDetected()
    {
        const string rejected = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1760000000,"rateLimitType":"five_hour"}}""";

        var summary = await Analyze(Init, rejected);

        Assert.True(summary.UsageLimitReached);
    }

    [Fact]
    public async Task AllowedRateLimitEvent_IsNotAUsageLimit()
    {
        const string allowed = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1760000000,"rateLimitType":"five_hour"}}""";

        var summary = await Analyze(Init, allowed, Result());

        Assert.False(summary.UsageLimitReached);
    }

    [Fact]
    public async Task LimitWordsInNormalAgentOutput_AreNotAUsageLimit()
    {
        var summary = await Analyze(Init, Assistant, Result(result: "The usage limit reached by the queue is 5; you hit your limit config"));

        Assert.False(summary.UsageLimitReached);
    }

    [Fact]
    public async Task ApiErrorAssistantMessage_WithLimitText_IsDetected()
    {
        const string apiError = """{"type":"assistant","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"Claude AI usage limit reached|1760000000"}]}}""";

        var summary = await Analyze(Init, apiError);

        Assert.True(summary.UsageLimitReached);
    }

    [Fact]
    public async Task AssistantEventWithRateLimitError_IsDetected()
    {
        const string rateLimited = """{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"You've hit your limit"}]}}""";

        var summary = await Analyze(Init, rateLimited);

        Assert.True(summary.UsageLimitReached);
        Assert.Equal("You've hit your limit", summary.UsageLimitMessage);
    }

    [Fact]
    public async Task AssistantEventWithRateLimitError_IsDetectedWithoutMessageText()
    {
        const string rateLimited = """{"type":"assistant","error":"rate_limit","message":{"content":[]}}""";

        var summary = await Analyze(Init, rateLimited);

        Assert.True(summary.UsageLimitReached);
        Assert.False(string.IsNullOrWhiteSpace(summary.UsageLimitMessage));
    }

    [Fact]
    public async Task AssistantEventWithAnotherError_IsNotAUsageLimit()
    {
        const string other = """{"type":"assistant","error":"invalid_request","message":{"content":[{"type":"text","text":"bad"}]}}""";

        var summary = await Analyze(Init, other, Result());

        Assert.False(summary.UsageLimitReached);
    }

    [Fact]
    public async Task OversizedResultText_IsClampedForTheTemporalPayload()
    {
        var summary = await Analyze(Init, Result(result: new string('a', 100_000)));

        Assert.False(summary.IsTruncated);
        Assert.True(summary.ResultText!.Length <= ClaudeOutputAnalyzer.MaxResultTextLength);
    }

    [Fact]
    public async Task ALineOverTheLineCap_IsSkippedAndCountedWhileTheRestIsRead()
    {
        var huge = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('a', BoundedLineReader.MaxLineChars + 10) + "\"}]}}";

        var summary = await Analyze(Init, huge, Result());

        Assert.Equal(1, summary.SkippedLines);
        Assert.Equal("done", summary.ResultText);
    }

    [Fact]
    public async Task ALineWithALoneSurrogateEscape_IsSkippedAndTheLaterResultIsStillRead()
    {
        const string unreadable = """{"type":"result","subtype":"success","is_error":false,"result":"\uD800"}""";

        var summary = await Analyze(Init, unreadable, Result());

        Assert.Equal("done", summary.ResultText);
        Assert.False(summary.IsTruncated);
    }

    [Theory]
    [InlineData("error_max_turns", "turn")]
    [InlineData("error_max_budget_usd", "budget")]
    public async Task ResultStoppedByACliLimit_ExposesTheLimitStop(string subtype, string expected)
    {
        var summary = await Analyze(Init, Result(subtype: subtype, isError: true));

        Assert.Contains(expected, summary.LimitStop, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulResult_HasNoLimitStop()
    {
        var summary = await Analyze(Init, Result());

        Assert.Null(summary.LimitStop);
    }
}
