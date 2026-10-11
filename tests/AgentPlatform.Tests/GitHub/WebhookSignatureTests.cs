using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.GitHub;

public class WebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"zen":"Keep it logically awesome."}""");

    [Fact]
    public void ASignatureOverTheBodyMadeWithTheSecret_IsValid()
    {
        var header = WebhookSignature.Compute(Body, GitHubPayloads.Secret);

        Assert.True(WebhookSignature.IsValid(Body, header, GitHubPayloads.Secret));
    }

    [Fact]
    public void ComputeMatchesAKnownHmacSha256Vector()
    {
        // RFC 4231 test case 2
        var header = WebhookSignature.Compute(Encoding.UTF8.GetBytes("what do ya want for nothing?"), "Jefe");

        Assert.Equal("sha256=5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", header);
    }

    [Fact]
    public void ASignatureMadeWithAnotherSecret_IsInvalid()
    {
        var header = WebhookSignature.Compute(Body, "another-secret");

        Assert.False(WebhookSignature.IsValid(Body, header, GitHubPayloads.Secret));
    }

    [Fact]
    public void ABodyChangedAfterSigning_IsInvalid()
    {
        var header = WebhookSignature.Compute(Body, GitHubPayloads.Secret);
        var tampered = Encoding.UTF8.GetBytes("""{"zen":"Keep it logically awesome!"}""");

        Assert.False(WebhookSignature.IsValid(tampered, header, GitHubPayloads.Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256=")]
    [InlineData("sha256=zz")]
    [InlineData("sha256=abcd")]
    [InlineData("sha1=5bdcc146bf60754e6a042426089575c75a003f08")]
    [InlineData("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843")]
    public void AMissingOrMalformedHeader_IsInvalid(string? header)
    {
        Assert.False(WebhookSignature.IsValid(Body, header, GitHubPayloads.Secret));
    }

    [Fact]
    public void TheHexDigestIsAcceptedInUpperCase()
    {
        var header = WebhookSignature.Compute(Body, GitHubPayloads.Secret);
        var upper = "sha256=" + header["sha256=".Length..].ToUpperInvariant();

        Assert.True(WebhookSignature.IsValid(Body, upper, GitHubPayloads.Secret));
    }

    [Fact]
    public void AnEmptySecret_NeverValidates()
    {
        var header = WebhookSignature.Compute(Body, string.Empty);

        Assert.False(WebhookSignature.IsValid(Body, header, string.Empty));
    }
}
