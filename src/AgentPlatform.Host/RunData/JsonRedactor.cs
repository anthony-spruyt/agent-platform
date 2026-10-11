using System.Buffers;
using System.Text;
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
        var raw = value.GetRawText();
        if (raw.Contains("\\u", StringComparison.Ordinal))
        {
            var cleaned = LoneSurrogateEscape().Replace(raw, static m => m.Groups["lone"].Success ? string.Empty : m.Value);
            if (!string.Equals(cleaned, raw, StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(cleaned, _readerOptions);
                return Write(document.RootElement, nesting);
            }
        }

        return Write(value, nesting);
    }

    private static string Write(JsonElement value, int nesting)
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

    private static string RedactName(string name) => CredentialShape().Replace(StripInvalid(name), Placeholder);

    private static string RedactString(string value, int nesting)
    {
        var text = StripInvalid(value);
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

    // A pathological input that times a pattern out is dropped whole rather than stored unchecked.
    private static string RedactPlainText(string text)
    {
        try
        {
            var redacted = PrivateKeyBlock().Replace(text, Placeholder);
            redacted = RedactKeyValues(redacted);
            redacted = UrlUserInfo().Replace(redacted, "${scheme}" + Placeholder + "@");
            redacted = UrlTokenUserInfo().Replace(redacted, "${scheme}" + Placeholder + "@");
            return CredentialShape().Replace(redacted, Placeholder);
        }
        catch (RegexMatchTimeoutException)
        {
            return Placeholder;
        }
    }

    // Free text and truncated JSON: any `name = value` or `name: value` whose name is sensitive loses its value.
    private static string RedactKeyValues(string text)
    {
        StringBuilder? result = null;
        var copied = 0;
        var search = 0;
        while (search < text.Length)
        {
            var key = KeyStart().Match(text, search);
            if (!key.Success)
            {
                break;
            }

            var valueStart = key.Index + key.Length;
            search = valueStart;
            var name = key.Groups["name"].Value;
            if (!SensitiveName().IsMatch(name))
            {
                continue;
            }

            var isCookieHeader = key.Groups["sep"].Value == ":" && name.Contains("cookie", StringComparison.OrdinalIgnoreCase);
            var value = (isCookieHeader ? LineValue() : KeyValue()).Match(text, valueStart);
            if (!value.Success || (TokenCountName().IsMatch(name) && NumericValue().IsMatch(value.Value)))
            {
                continue;
            }

            result ??= new StringBuilder(text.Length);
            var quote = value.Groups["quote"];
            result.Append(text, copied, valueStart - copied);
            result.Append(quote.Value).Append(Placeholder).Append(quote.Value);
            copied = valueStart + value.Length;
            search = copied;
        }

        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    private static string StripInvalid(string value)
    {
        if (!HasInvalidChar(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else if (!char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool HasInvalidChar(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (c == '\0' || char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

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
        @"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|\bsl_\w{16,}|(?i:basic|bearer)(?:\s|%20|%09|\+)+[A-Za-z0-9._~+/=%-]{16,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bv2\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.\d+\.[A-Za-z0-9_-]{20,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialShape();

    // JSON escapes are walked in order so an escaped backslash is never read as the start of a \uD800.
    [GeneratedRegex(@"\\u[dD][89abAB][0-9a-fA-F]{2}\\u[dD][c-fC-F][0-9a-fA-F]{2}|(?<lone>\\u[dD][89a-fA-F][0-9a-fA-F]{2})|\\.", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoneSurrogateEscape();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----.*?(?:-----END [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----|\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<![\w.-])[\\""']*(?<name>[\w.-]+)[\\""']*[ \t]*(?<sep>[:=])[ \t]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyStart();

    [GeneratedRegex(
        @"\G(?:(?<quote>\\*[""'])(?:(?!\k<quote>)(?:\\.|[^\\]))*(?:\k<quote>|\z)|(?:(?i:bearer|basic|token|digest|negotiate)(?:[ \t]|%20|%09|\+)+)?[^\s;&""']+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\G[^\r\n]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineValue();

    [GeneratedRegex(@"^-?\d+(?:\.\d+)?[,}\])]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumericValue();

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.-]*://)[^/\s:@]*:[^/\s]+@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlUserInfo();

    // ssh-style schemes carry a user name, not a secret, in the userinfo.
    [GeneratedRegex(@"(?<scheme>\b(?!(?i:ssh|sftp|scp|rsync|git\+ssh|ssh\+git)://)[A-Za-z][A-Za-z0-9+.-]*://)[^/\s:@?#]+@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlTokenUserInfo();
}
