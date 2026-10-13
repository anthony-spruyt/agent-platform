using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class AgentSessionTokensTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Now.AddHours(1);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AgentSessionTokens At(DateTimeOffset now, string key = Key) => new(key, new FixedTime(now));

    [Fact]
    public void Issue_ThenValidate_ReturnsTheRun()
    {
        var tokens = At(Now);

        var token = tokens.Issue("renovate-triage/anthony-spruyt/repo/1", "run-1", "claude", 1, Expiry);

        Assert.True(tokens.TryValidate(token, out var run));
        Assert.Equal(new AgentRunIdentity("renovate-triage/anthony-spruyt/repo/1", "run-1", "claude", 1), run);
    }

    [Fact]
    public void Issue_IsDeterministicSoRetriesReuseTheToken()
    {
        var tokens = At(Now);

        Assert.Equal(tokens.Issue("wf", "run", "claude", 1, Expiry), tokens.Issue("wf", "run", "claude", 1, Expiry));
    }

    [Fact]
    public void Issue_DiffersPerRunAndPerExpiry()
    {
        var tokens = At(Now);

        Assert.NotEqual(tokens.Issue("wf", "run-1", "claude", 1, Expiry), tokens.Issue("wf", "run-2", "claude", 1, Expiry));
        Assert.NotEqual(tokens.Issue("wf-1", "run", "claude", 1, Expiry), tokens.Issue("wf-2", "run", "claude", 1, Expiry));
        Assert.NotEqual(tokens.Issue("wf", "run", "claude", 1, Expiry), tokens.Issue("wf", "run", "claude", 1, Expiry.AddMinutes(1)));
    }

    [Fact]
    public void Issue_DiffersPerStepAndAttempt()
    {
        var tokens = At(Now);

        Assert.NotEqual(tokens.Issue("wf", "run", "claude", 1, Expiry), tokens.Issue("wf", "run", "claude", 2, Expiry));
        Assert.NotEqual(tokens.Issue("wf", "run", "claude", 1, Expiry), tokens.Issue("wf", "run", "other", 1, Expiry));
    }

    [Fact]
    public void TryValidate_ReturnsTheStepAndAttemptTheTokenWasIssuedFor()
    {
        var tokens = At(Now);

        Assert.True(tokens.TryValidate(tokens.Issue("wf", "run", "claude", 2, Expiry), out var run));
        Assert.Equal("claude", run.Step);
        Assert.Equal(2, run.Attempt);
    }

    [Fact]
    public void TryValidate_RejectsAnAttemptNumberSwappedIntoAnotherAttemptsToken()
    {
        var tokens = At(Now);
        var first = tokens.Issue("wf", "run", "claude", 1, Expiry).Split('.');
        var second = tokens.Issue("wf", "run", "claude", 2, Expiry).Split('.');
        first[4] = second[4];

        Assert.False(tokens.TryValidate(string.Join('.', first), out _));
    }

    [Fact]
    public void TryValidate_RejectsTheTokenFormatWithoutAStepAndAttempt()
    {
        Assert.False(At(Now).TryValidate("v2.d2Y.cnVu.1893499200.AAAA", out _));
    }

    [Fact]
    public void TryValidate_AcceptsATokenUntilItsExpiry()
    {
        var token = At(Now).Issue("wf", "run", "claude", 1, Expiry);

        Assert.True(At(Expiry.AddSeconds(-1)).TryValidate(token, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3600)]
    public void TryValidate_RejectsAnExpiredToken(int secondsPastExpiry)
    {
        var token = At(Now).Issue("wf", "run", "claude", 1, Expiry);

        Assert.False(At(Expiry.AddSeconds(secondsPastExpiry)).TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsTokenSignedWithAnotherKey()
    {
        var token = At(Now).Issue("wf", "run", "claude", 1, Expiry);

        Assert.False(At(Now, "another-key-another-key-another-key").TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsTamperedIdentity()
    {
        var tokens = At(Now);
        var genuine = tokens.Issue("wf", "run", "claude", 1, Expiry);
        var other = tokens.Issue("other-wf", "run", "claude", 1, Expiry);
        var parts = genuine.Split('.');
        var forged = string.Join('.', [parts[0], other.Split('.')[1], .. parts[2..]]);

        Assert.False(tokens.TryValidate(forged, out _));
    }

    [Fact]
    public void TryValidate_RejectsAnExtendedExpiry()
    {
        var tokens = At(Now);
        var genuine = tokens.Issue("wf", "run", "claude", 1, Expiry).Split('.');
        var later = tokens.Issue("wf", "run", "claude", 1, Expiry.AddDays(30)).Split('.');
        genuine[^2] = later[^2];

        Assert.False(tokens.TryValidate(string.Join('.', genuine), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v1.a.b")]
    [InlineData("v1.a.b.c.d")]
    [InlineData("v3.d2Y.cnVu.AAAA")]
    [InlineData("v3.d2Y.cnVu.Y2xhdWRl.1.not-a-number.AAAA")]
    [InlineData("v3.!!!.???.!!!.1.1.***")]
    public void TryValidate_RejectsMalformedTokens(string token)
    {
        Assert.False(At(Now).TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsNull()
    {
        Assert.False(At(Now).TryValidate(null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Constructor_RejectsWeakKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => new AgentSessionTokens(key));
    }
}
