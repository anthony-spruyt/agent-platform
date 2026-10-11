using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RunData;

internal static partial class NulStripping
{
    public static string Strip(string value) => value.Replace("\0", string.Empty, StringComparison.Ordinal);

    public static string StripJson(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Contains("\\u0000", StringComparison.Ordinal) ? NulEscape().Replace(raw, "$1") : raw;
    }

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000")]
    private static partial Regex NulEscape();
}
