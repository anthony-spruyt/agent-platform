using System.IO.Compression;
using System.Text;

namespace AgentPlatform.Host.Livemap;

public sealed record CompatArtifact(string ReportJson, string? BuildsJson);

public sealed class CompatArtifactException(string message, Exception? innerException = null) : Exception(message, innerException);

public static class CompatArtifactZip
{
    public const int MaxFileBytes = 262144;

    private const string ReportName = "report.json";

    private const string BuildsName = "builds.json";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CompatArtifact Parse(byte[] zip)
    {
        ArgumentNullException.ThrowIfNull(zip);

        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is not (ReportName or BuildsName) || files.ContainsKey(entry.FullName))
                {
                    throw new CompatArtifactException("The compat artifact holds an unexpected file.");
                }

                files[entry.FullName] = ReadBounded(entry);
            }

            return files.TryGetValue(ReportName, out var report)
                ? new CompatArtifact(report, files.GetValueOrDefault(BuildsName))
                : throw new CompatArtifactException("The compat artifact holds no report.");
        }
        catch (InvalidDataException ex)
        {
            throw new CompatArtifactException("The compat artifact is not a readable zip.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new CompatArtifactException("The compat artifact holds a file that is not UTF-8.", ex);
        }
    }

    private static string ReadBounded(ZipArchiveEntry entry)
    {
        using var content = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new CompatArtifactException("The compat artifact holds a file over the size cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return _strictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
