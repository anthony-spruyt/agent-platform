using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPlatform.Host.RunData;

// The run data is readable through a read-only tool, so credential material must never reach it.
public static partial class JsonRedactor
{
    public const string Placeholder = "[redacted]";

    private static readonly JsonWriterOptions _writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Redact(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            Write(value, writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Value.ValueKind != JsonValueKind.Null && SensitiveName().IsMatch(property.Name))
                    {
                        writer.WriteStringValue(Placeholder);
                    }
                    else
                    {
                        Write(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(CredentialShape().Replace(value.GetString()!, Placeholder));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    [GeneratedRegex("token(?!s)|secret|passw(or)?d|credential|authorization|cookie|signature|api[-_]?key|private[-_]?key|access[-_]?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(
        @"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{16,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bv2\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.\d+\.[A-Za-z0-9_-]{20,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialShape();
}
