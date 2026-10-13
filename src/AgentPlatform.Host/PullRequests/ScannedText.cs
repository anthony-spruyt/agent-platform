using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.PullRequests;

public sealed record ScannedText(string Hash, string Text)
{
    public bool IsIntact => string.Equals(Hash, HashOf(Text), StringComparison.Ordinal);

    internal static ScannedText Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ScannedText(HashOf(text), text);
    }

    private static string HashOf(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
