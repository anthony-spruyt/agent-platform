using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.GitHub;

public static class WebhookSignature
{
    public const string HeaderName = "X-Hub-Signature-256";

    private const string Prefix = "sha256=";

    public static string Compute(ReadOnlySpan<byte> body, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }

    public static bool IsValid(ReadOnlySpan<byte> body, string? header, string secret)
    {
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
