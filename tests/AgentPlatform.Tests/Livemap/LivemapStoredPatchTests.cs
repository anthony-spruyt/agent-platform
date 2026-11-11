using System.Text.Json;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapStoredPatchTests
{
    private static LivemapResult Data() => new(LivemapResultStatus.Data, "Added the row.", """{ "builds": [] }""", [], "kept in the run only");

    private static LivemapResult Code() =>
        new(LivemapResultStatus.Code, "Moved the hook.", """{ "builds": [] }""", [new LivemapFile("src/scan.cpp", "// größe \"quoted\"\n"), new LivemapFile("tests/scan_test.cpp", "int x;\n")], null);

    [Fact]
    public void ADataResult_ComesBackAsItWasStored_WithoutItsNotes()
    {
        var read = LivemapStoredPatch.Read(LivemapStoredPatch.Write(Data()));

        Assert.NotNull(read);
        Assert.Equal((LivemapResultStatus.Data, "Added the row.", """{ "builds": [] }"""), (read.Status, read.Summary, read.BuildsJson));
        Assert.Empty(read.Files);
        Assert.Null(read.Notes);
    }

    [Fact]
    public void ACodeResult_KeepsEveryFile_PathAndContentExactly()
    {
        var read = LivemapStoredPatch.Read(LivemapStoredPatch.Write(Code()));

        Assert.NotNull(read);
        Assert.Equal(LivemapResultStatus.Code, read.Status);
        Assert.Equal(Code().Files, read.Files);
    }

    [Fact]
    public void TheStatus_IsStoredAsText_SoReorderingTheEnumCannotChangeAStoredPatch()
    {
        using var stored = JsonDocument.Parse(LivemapStoredPatch.Write(Code()));

        Assert.Equal("code", stored.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(LivemapResultStatus.Partial)]
    [InlineData(LivemapResultStatus.GaveUp)]
    public void OnlyADataOrCodeResult_CanBeStored(LivemapResultStatus status)
    {
        var result = new LivemapResult(status, "Stuck.", """{ "builds": [] }""", [], "notes");

        Assert.Throws<ArgumentException>(() => LivemapStoredPatch.Write(result));
    }

    [Fact]
    public void AResultWithoutABuildsFile_CannotBeStored()
    {
        Assert.Throws<ArgumentException>(() => LivemapStoredPatch.Write(Data() with { BuildsJson = null }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "status": "partial", "summary": "s", "builds_json": "{}", "files": [] }""")]
    [InlineData("""{ "status": 1, "summary": "s", "builds_json": "{}", "files": [] }""")]
    [InlineData("""{ "status": "data", "summary": "s", "files": [] }""")]
    [InlineData("""{ "status": "data", "summary": "s", "builds_json": "{}", "files": [ { "path": "a" } ] }""")]
    public void AnythingElse_ReadsAsNothing(string? stored)
    {
        Assert.Null(LivemapStoredPatch.Read(stored));
    }
}
