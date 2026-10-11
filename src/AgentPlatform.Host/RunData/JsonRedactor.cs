using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RunData;

// The run data is readable through a read-only tool, so credential material must never reach it.
public static partial class JsonRedactor
{
    public const string Placeholder = "[redacted]";

    // Each level of JSON-in-a-string doubles the escaping, so real data never nests this deep.
    private const int MaxStringNesting = 8;

    private static readonly JsonWriterOptions _writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions _readerOptions = new() { MaxDepth = 64 };

    public static string Redact(JsonElement value) => Redact(value, 0);

    public static string RedactText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RedactString(value, 0);
    }

    private static string Redact(JsonElement value, int nesting)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            Write(value, writer, nesting);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer, int nesting)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(value, writer, nesting);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer, nesting);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactString(value.GetString()!, nesting));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(JsonElement value, Utf8JsonWriter writer, int nesting)
    {
        var entryIsSensitive = NamesASensitiveEntry(value);
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(RedactName(property.Name));
            var redactEntryValue = entryIsSensitive && property.Name.Equals("value", StringComparison.OrdinalIgnoreCase);
            if (property.Value.ValueKind != JsonValueKind.Null && (redactEntryValue || IsSensitive(property.Name, property.Value)))
            {
                writer.WriteStringValue(Placeholder);
            }
            else
            {
                Write(property.Value, writer, nesting);
            }
        }

        writer.WriteEndObject();
    }

    // {"name":"PGPASSWORD","value":"..."}: the name says what the value is.
    private static bool NamesASensitiveEntry(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("key", StringComparison.OrdinalIgnoreCase))
                && SensitiveName().IsMatch(property.Value.GetString()!))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSensitive(string name, JsonElement value) =>
        SensitiveName().IsMatch(name) && !(value.ValueKind == JsonValueKind.Number && TokenCountName().IsMatch(name));

    private static string RedactName(string name) => CredentialShape().Replace(StripNul(name), Placeholder);

    private static string RedactString(string value, int nesting)
    {
        var text = StripNul(value);
        if (LooksLikeJsonContainer(text))
        {
            if (nesting >= MaxStringNesting)
            {
                return Placeholder;
            }

            if (TryParse(text, out var parsed))
            {
                using (parsed)
                {
                    return Redact(parsed.RootElement, nesting + 1);
                }
            }
        }

        return RedactPlainText(text);
    }

    private static string RedactPlainText(string text)
    {
        var redacted = PrivateKeyBlock().Replace(text, Placeholder);
        redacted = KeyValueSecret().Replace(redacted, "${key}=" + Placeholder);
        redacted = UrlUserInfo().Replace(redacted, "${scheme}" + Placeholder + "@");
        return CredentialShape().Replace(redacted, Placeholder);
    }

    private static string StripNul(string value) => value.Contains('\0', StringComparison.Ordinal) ? value.Replace("\0", string.Empty, StringComparison.Ordinal) : value;

    private static bool LooksLikeJsonContainer(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length > 1 && (trimmed[0] is '{' or '[');
    }

    private static bool TryParse(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text, _readerOptions);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    [GeneratedRegex(
        @"token(?!s?_?count|izer|s_(?:in|out))|secret(?!_?name)|passw(?:or)?d|passphrase|\bpwd|pwd\b|(?<![a-z])pass(?![a-z])|(?<![a-z])auth(?![a-z])|(?-i:auth(?=[A-Z]))|credential|authorization|cookie|signature|api[-_]?key|private[-_]?key|access[-_]?key|connection[-_ ]?string|(?<![a-z])dsn(?![a-z])|(?<![a-z])session(?:[-_]?(?:id|key))?$|litellm[-_]?key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SensitiveName();

    [GeneratedRegex("tokens$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenCountName();

    [GeneratedRegex(
        @"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|\bsl_\w{16,}|(?:Basic\s+|(?i:bearer)(?:\s|%20|\+)+)[A-Za-z0-9._~+/=%-]{16,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bv2\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.\d+\.[A-Za-z0-9_-]{20,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialShape();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<key>[A-Za-z_]*(?:password|passwd|pwd))\s*=\s*(?:""[^""]*""|'[^']*'|[^;&\s""']*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.-]*://)[^/\s:@]*:[^/\s]+@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlUserInfo();
}
