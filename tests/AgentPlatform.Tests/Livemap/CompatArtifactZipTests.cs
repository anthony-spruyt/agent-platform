using System.IO.Compression;
using System.Text;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class CompatArtifactZipTests
{
    private static (string Name, byte[] Content) Text(string name, string content) => (name, Encoding.UTF8.GetBytes(content));

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = entry.Open();
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void ReadsTheReportAndTheBuilds()
    {
        var artifact = CompatArtifactZip.Parse(Zip(Text("report.json", """{"result":"easy"}"""), Text("builds.json", """{"builds":[]}""")));

        Assert.Equal("""{"result":"easy"}""", artifact.ReportJson);
        Assert.Equal("""{"builds":[]}""", artifact.BuildsJson);
    }

    [Fact]
    public void ABuildsFileIsOptional_ForAHardResult()
    {
        var artifact = CompatArtifactZip.Parse(Zip(Text("report.json", """{"result":"hard"}""")));

        Assert.Equal("""{"result":"hard"}""", artifact.ReportJson);
        Assert.Null(artifact.BuildsJson);
    }

    [Fact]
    public void WithoutAReport_IsRefused()
    {
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse(Zip(Text("builds.json", "{}"))));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("nested/report.json")]
    [InlineData("../report.json")]
    [InlineData("Report.json")]
    public void AnyOtherEntry_IsRefused(string name)
    {
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse(Zip(Text("report.json", "{}"), Text(name, "{}"))));
    }

    [Fact]
    public void ARepeatedEntry_IsRefused()
    {
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse(Zip(Text("report.json", """{"result":"hard"}"""), Text("report.json", """{"result":"easy"}"""))));
    }

    [Fact]
    public void AFileOverTheCap_IsRefused_EvenWhenItCompressesToAFewBytes()
    {
        var oversized = new byte[CompatArtifactZip.MaxFileBytes + 1];
        Array.Fill(oversized, (byte)' ');
        var zip = Zip(Text("report.json", "{}"), ("builds.json", oversized));

        Assert.True(zip.Length < 4096, "the bomb must be small on the wire");
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse(zip));
    }

    [Fact]
    public void AFileAtTheCap_IsRead()
    {
        var padding = new string(' ', CompatArtifactZip.MaxFileBytes - 2);
        var artifact = CompatArtifactZip.Parse(Zip(Text("report.json", "{}" + padding)));

        Assert.Equal(CompatArtifactZip.MaxFileBytes, artifact.ReportJson.Length);
    }

    [Fact]
    public void AFileThatIsNotUtf8_IsRefused()
    {
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse(Zip(("report.json", [0x7B, 0xFF, 0xFE, 0x7D]))));
    }

    [Fact]
    public void BytesThatAreNotAZip_AreRefused()
    {
        Assert.Throws<CompatArtifactException>(() => CompatArtifactZip.Parse("not a zip"u8.ToArray()));
    }
}
