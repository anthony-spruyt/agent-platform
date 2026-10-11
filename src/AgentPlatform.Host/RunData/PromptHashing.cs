using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.RunData;

public static class PromptHashing
{
    public static string Compute(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }
}
