using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.Agents;

public sealed class AgentSessionTokens
{
    private const string Version = "v1";
    private const int MinimumKeyLength = 32;

    private readonly byte[] _key;

    public AgentSessionTokens(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumKeyLength)
        {
            throw new ArgumentException($"The session token key must be at least {MinimumKeyLength} characters.", nameof(key));
        }

        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Issue(string workflowId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return $"{Version}.{Encode(workflowId)}.{Encode(runId)}.{Base64Url(Sign(workflowId, runId))}";
    }

    public bool TryValidate(string? token, out AgentRunIdentity run)
    {
        run = null!;
        var parts = token?.Split('.');
        if (parts is not [Version, var encodedWorkflow, var encodedRun, var encodedMac])
        {
            return false;
        }

        if (!TryDecode(encodedWorkflow, out var workflowId)
            || !TryDecode(encodedRun, out var runId)
            || !TryDecodeBytes(encodedMac, out var mac)
            || workflowId.Length == 0
            || runId.Length == 0)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(mac, Sign(workflowId, runId)))
        {
            return false;
        }

        run = new AgentRunIdentity(workflowId, runId);
        return true;
    }

    private byte[] Sign(string workflowId, string runId) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{Version}\n{workflowId}\n{runId}"));

    private static string Encode(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        if (!TryDecodeBytes(value, out var bytes))
        {
            return false;
        }

        try
        {
            decoded = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDecodeBytes(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        bytes = new byte[padded.Length];
        if (Convert.TryFromBase64String(padded, bytes, out var written))
        {
            bytes = bytes[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
