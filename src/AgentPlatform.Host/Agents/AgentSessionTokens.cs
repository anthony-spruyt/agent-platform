using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentPlatform.Host.Agents;

public sealed class AgentSessionTokens
{
    private const string Version = "v3";
    private const int MinimumKeyLength = 32;

    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public AgentSessionTokens(string key, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumKeyLength)
        {
            throw new ArgumentException($"The session token key must be at least {MinimumKeyLength} characters.", nameof(key));
        }

        _key = Encoding.UTF8.GetBytes(key);
        _time = time ?? TimeProvider.System;
    }

    public string Issue(string workflowId, string runId, string step, int attempt, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var expiry = expiresAt.ToUnixTimeSeconds();
        var mac = Base64Url(Sign(workflowId, runId, step, attempt, expiry));
        return $"{Version}.{Encode(workflowId)}.{Encode(runId)}.{Encode(step)}.{attempt.ToString(CultureInfo.InvariantCulture)}.{expiry.ToString(CultureInfo.InvariantCulture)}.{mac}";
    }

    public bool TryValidate(string? token, out AgentRunIdentity run)
    {
        run = null!;
        var parts = token?.Split('.');
        if (parts is not [Version, var encodedWorkflow, var encodedRun, var encodedStep, var encodedAttempt, var encodedExpiry, var encodedMac])
        {
            return false;
        }

        if (!TryDecode(encodedWorkflow, out var workflowId)
            || !TryDecode(encodedRun, out var runId)
            || !TryDecode(encodedStep, out var step)
            || !TryDecodeBytes(encodedMac, out var mac)
            || !int.TryParse(encodedAttempt, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            || !long.TryParse(encodedExpiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
            || workflowId.Length == 0
            || runId.Length == 0
            || step.Length == 0
            || attempt < 1)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(mac, Sign(workflowId, runId, step, attempt, expiry))
            || _time.GetUtcNow().ToUnixTimeSeconds() >= expiry)
        {
            return false;
        }

        run = new AgentRunIdentity(workflowId, runId, step, attempt);
        return true;
    }

    private byte[] Sign(string workflowId, string runId, string step, int attempt, long expiry) =>
        HMACSHA256.HashData(
            _key,
            Encoding.UTF8.GetBytes($"{Version}\n{workflowId}\n{runId}\n{step}\n{attempt.ToString(CultureInfo.InvariantCulture)}\n{expiry.ToString(CultureInfo.InvariantCulture)}"));

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
