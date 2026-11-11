using System.Text.Json;
using AgentPlatform.Host.Agents;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapResultTests
{
    private const string BuildsJson = """{"builds":[]}""";

    private static SubmitResultPayload Payload(object data, string status = "success", string summary = "Added the row.") =>
        new(status, summary, JsonSerializer.SerializeToElement(data));

    private static object[] Files(params string[] paths) => [.. paths.Select(path => new { path, content = "int x;" })];

    private static object CodeData(params string[] paths) =>
        new { status = "code", builds_json = BuildsJson, files = Files(paths) };

    private static string? Error(SubmitResultPayload payload) =>
        LivemapResult.TryParse(payload, out _, out var error) ? null : error;

    [Fact]
    public void TryParse_AcceptsADataResult()
    {
        Assert.True(LivemapResult.TryParse(Payload(new { status = "data", builds_json = BuildsJson }), out var result, out var error), error);

        Assert.Equal(LivemapResultStatus.Data, result!.Status);
        Assert.Equal(BuildsJson, result.BuildsJson);
        Assert.Equal("Added the row.", result.Summary);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void TryParse_AcceptsACodeResultWithItsFiles()
    {
        Assert.True(LivemapResult.TryParse(Payload(CodeData("src/scan.cpp", "tests/test_scan.py")), out var result, out var error), error);

        Assert.Equal(LivemapResultStatus.Code, result!.Status);
        Assert.Equal(BuildsJson, result.BuildsJson);
        Assert.Equal(["src/scan.cpp", "tests/test_scan.py"], result.Files.Select(f => f.Path));
        Assert.All(result.Files, f => Assert.Equal("int x;", f.Content));
    }

    [Fact]
    public void TryParse_AcceptsAPartialResultAndKeepsItsNotes()
    {
        Assert.True(LivemapResult.TryParse(Payload(new { status = "partial", notes = "Found the table at 0x10; the hook still moves." }), out var result, out var error), error);

        Assert.Equal(LivemapResultStatus.Partial, result!.Status);
        Assert.Equal("Found the table at 0x10; the hook still moves.", result.Notes);
        Assert.Null(result.BuildsJson);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void TryParse_AcceptsAGaveUpResultWithoutNotes()
    {
        Assert.True(LivemapResult.TryParse(Payload(new { status = "gave_up" }), out var result, out var error), error);

        Assert.Equal(LivemapResultStatus.GaveUp, result!.Status);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void TryParse_IgnoresTheFilesOfAResultThatCarriesNoPatch()
    {
        Assert.True(LivemapResult.TryParse(Payload(new { status = "partial", notes = "Still working.", builds_json = BuildsJson, files = Files("src/a.cpp") }), out var result, out var error), error);

        Assert.Null(result!.BuildsJson);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void TryParse_RejectsAFailureStatus()
    {
        Assert.Contains("\"success\"", Error(Payload(new { status = "gave_up" }, status: "failure")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data")]
    [InlineData("done")]
    public void TryParse_RejectsAnUnknownResultStatus(string status)
    {
        Assert.Contains("data.status", Error(Payload(new { status, builds_json = BuildsJson })));
    }

    [Fact]
    public void TryParse_RejectsAMissingResultStatus()
    {
        Assert.Contains("data.status", Error(Payload(new { builds_json = BuildsJson })));
    }

    [Fact]
    public void TryParse_RejectsDataThatIsNotAnObject()
    {
        var payload = new SubmitResultPayload("success", "Done.", JsonSerializer.SerializeToElement("text"));

        Assert.Contains("JSON object", Error(payload));
        Assert.Contains("JSON object", Error(new SubmitResultPayload("success", "Done.", null)));
    }

    [Fact]
    public void TryParse_RejectsABlankSummary()
    {
        Assert.Contains("summary", Error(Payload(new { status = "gave_up" }, summary: " ")));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("code")]
    public void TryParse_RejectsAPatchWithoutTheBuildsFile(string status)
    {
        Assert.Contains("builds_json", Error(Payload(new { status, files = Files("src/a.cpp") })));
        Assert.Contains("builds_json", Error(Payload(new { status, builds_json = " ", files = Files("src/a.cpp") })));
        Assert.Contains("builds_json", Error(Payload(new { status, builds_json = 3, files = Files("src/a.cpp") })));
    }

    [Fact]
    public void TryParse_RejectsFilesOnADataResult()
    {
        Assert.Contains("code", Error(Payload(new { status = "data", builds_json = BuildsJson, files = Files("src/a.cpp") })));
    }

    [Fact]
    public void TryParse_RejectsACodeResultWithoutFiles()
    {
        Assert.Contains("files", Error(Payload(new { status = "code", builds_json = BuildsJson })));
        Assert.Contains("files", Error(Payload(new { status = "code", builds_json = BuildsJson, files = Array.Empty<object>() })));
    }

    [Fact]
    public void TryParse_RejectsAPartialResultWithoutNotes()
    {
        Assert.Contains("notes", Error(Payload(new { status = "partial" })));
        Assert.Contains("notes", Error(Payload(new { status = "partial", notes = "  " })));
    }

    [Theory]
    [InlineData("src/scan.cpp")]
    [InlineData("src/nested/dir/a.h")]
    [InlineData("tests/test_scan.py")]
    [InlineData("tools/selftest.cpp")]
    [InlineData("data/extra.json")]
    [InlineData("src/a-b_c.v1.cpp")]
    public void TryParse_AcceptsPathsInTheAllowlist(string path)
    {
        Assert.Null(Error(Payload(CodeData(path))));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/../.github/workflows/ci.yml")]
    [InlineData("data/../.github/workflows/ci.yml")]
    [InlineData("/src/a.cpp")]
    [InlineData("src\\a.cpp")]
    [InlineData("src//a.cpp")]
    [InlineData("src/./a.cpp")]
    [InlineData("src/")]
    [InlineData("src/a.cpp ")]
    [InlineData("SRC/a.cpp")]
    [InlineData("tools/other.cpp")]
    [InlineData("tools/selftest.cpp/x")]
    [InlineData("tools/compat/run.py")]
    [InlineData("README.md")]
    [InlineData("CMakeLists.txt")]
    [InlineData("srcs/a.cpp")]
    [InlineData("src/a\nb.cpp")]
    [InlineData("src/a@b.cpp")]
    [InlineData("src/a+b.cpp")]
    [InlineData("src/a~b.cpp")]
    [InlineData("")]
    public void TryParse_RejectsPathsOutsideTheAllowlist(string path)
    {
        Assert.Contains(path.Length == 0 ? "path" : "allowed", Error(Payload(CodeData(path))), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsTheBuildsFileAmongTheFiles()
    {
        Assert.Contains("builds_json", Error(Payload(CodeData("data/builds.json"))));
    }

    [Theory]
    [InlineData("src/a.cpp", "src/a.cpp")]
    [InlineData("src/a.cpp", "src/A.cpp")]
    [InlineData("src/a", "src/a/b.cpp")]
    public void TryParse_RejectsPathsThatClashWithEachOther(string first, string second)
    {
        var error = Error(Payload(CodeData(first, second)));

        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsAFileThatIsNotAPathAndContentPair()
    {
        Assert.Contains("path", Error(Payload(new { status = "code", builds_json = BuildsJson, files = new object[] { "src/a.cpp" } })));
        Assert.Contains("content", Error(Payload(new { status = "code", builds_json = BuildsJson, files = new object[] { new { path = "src/a.cpp" } } })));
        Assert.Contains("content", Error(Payload(new { status = "code", builds_json = BuildsJson, files = new object[] { new { path = "src/a.cpp", content = 7 } } })));
        Assert.Contains("files", Error(Payload(new { status = "code", builds_json = BuildsJson, files = "src/a.cpp" })));
    }

    [Fact]
    public void TryParse_AcceptsAnEmptyFile()
    {
        Assert.True(LivemapResult.TryParse(Payload(new { status = "code", builds_json = BuildsJson, files = new object[] { new { path = "tests/__init__.py", content = "" } } }), out var result, out var error), error);

        Assert.Equal("", Assert.Single(result!.Files).Content);
    }

    [Fact]
    public void TryParse_RejectsTooManyFiles()
    {
        var paths = Enumerable.Range(0, LivemapResult.MaxFiles + 1).Select(i => $"src/f{i}.cpp").ToArray();

        Assert.Contains($"{LivemapResult.MaxFiles}", Error(Payload(CodeData(paths))));
        Assert.Null(Error(Payload(CodeData(paths[..LivemapResult.MaxFiles]))));
    }

    [Fact]
    public void TryParse_RejectsAFileOverTheSizeCap()
    {
        var big = new string('x', LivemapResult.MaxFileLength + 1);
        var data = new { status = "code", builds_json = BuildsJson, files = new object[] { new { path = "src/a.cpp", content = big } } };

        Assert.Contains("src/a.cpp", Error(Payload(data)));
        Assert.Null(Error(Payload(new { status = "code", builds_json = BuildsJson, files = new object[] { new { path = "src/a.cpp", content = big[1..] } } })));
    }

    [Fact]
    public void TryParse_RejectsFilesThatTogetherPassTheSizeCap()
    {
        var chunk = new string('x', LivemapResult.MaxFileLength);
        var count = (LivemapResult.MaxFilesLength / LivemapResult.MaxFileLength) + 1;
        var files = Enumerable.Range(0, count).Select(i => new { path = $"src/f{i}.cpp", content = chunk }).ToArray();

        Assert.Contains("together", Error(Payload(new { status = "code", builds_json = BuildsJson, files })));
    }

    [Fact]
    public void TryParse_RejectsBuildsJsonOverTheSizeCap()
    {
        var big = new string('x', LivemapResult.MaxBuildsJsonLength + 1);

        Assert.Contains("builds_json", Error(Payload(new { status = "data", builds_json = big })));
        Assert.Null(Error(Payload(new { status = "data", builds_json = big[1..] })));
    }

    [Fact]
    public void TryParse_RejectsLongNotesAndSummaries()
    {
        Assert.Contains("notes", Error(Payload(new { status = "partial", notes = new string('n', LivemapResult.MaxNotesLength + 1) })));
        Assert.Contains("summary", Error(Payload(new { status = "gave_up" }, summary: new string('s', LivemapResult.MaxSummaryLength + 1))));
    }
}
