using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class AgentSessionTokensTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Issue_ThenValidate_ReturnsTheRun()
    {
        var tokens = new AgentSessionTokens(Key);

        var token = tokens.Issue("renovate-triage/anthony-spruyt/repo/1", "run-1");

        Assert.True(tokens.TryValidate(token, out var run));
        Assert.Equal(new AgentRunIdentity("renovate-triage/anthony-spruyt/repo/1", "run-1"), run);
    }

    [Fact]
    public void Issue_IsDeterministicSoRetriesReuseTheToken()
    {
        var tokens = new AgentSessionTokens(Key);

        Assert.Equal(tokens.Issue("wf", "run"), tokens.Issue("wf", "run"));
    }

    [Fact]
    public void Issue_DiffersPerRun()
    {
        var tokens = new AgentSessionTokens(Key);

        Assert.NotEqual(tokens.Issue("wf", "run-1"), tokens.Issue("wf", "run-2"));
        Assert.NotEqual(tokens.Issue("wf-1", "run"), tokens.Issue("wf-2", "run"));
    }

    [Fact]
    public void TryValidate_RejectsTokenSignedWithAnotherKey()
    {
        var token = new AgentSessionTokens(Key).Issue("wf", "run");

        Assert.False(new AgentSessionTokens("another-key-another-key-another-key").TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsTamperedIdentity()
    {
        var tokens = new AgentSessionTokens(Key);
        var genuine = tokens.Issue("wf", "run");
        var other = tokens.Issue("other-wf", "run");
        var parts = genuine.Split('.');
        var forged = string.Join('.', parts[0], other.Split('.')[1], parts[2], parts[3]);

        Assert.False(tokens.TryValidate(forged, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v1.a.b")]
    [InlineData("v1.a.b.c.d")]
    [InlineData("v2.d2Y.cnVu.AAAA")]
    [InlineData("v1.!!!.???.***")]
    public void TryValidate_RejectsMalformedTokens(string token)
    {
        Assert.False(new AgentSessionTokens(Key).TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsNull()
    {
        Assert.False(new AgentSessionTokens(Key).TryValidate(null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Constructor_RejectsWeakKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => new AgentSessionTokens(key));
    }
}
