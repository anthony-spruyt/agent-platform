using System.Text.Json.Nodes;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapPatchValidatorTests
{
    private const long BuildId = 400;
    private const string Gid = "444";
    private const string ShaNew = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ShaOld = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SchemaText = """{"title":"base schema"}""";

    private const string BaseBuilds = $$$"""
        {
          "$schema": "./builds.schema.json",
          "sigsets": {
            "launch": [{"site": "player_table", "bytes": "48 8b", "offset": "0x0", "mandatory": true}],
            "patch1": [{"site": "player_table", "bytes": "48 8c", "offset": "0x0", "mandatory": true}]
          },
          "builds": [
            {"label": "300", "buildids": ["300"], "time_date_stamp": 1000, "size_of_image": "0x2000", "sigset": "launch", "mining_layout": "inline_array",
             "steam": {"depot": "{{{SteamAppInfoClient.DepotId}}}", "manifest": "333", "exe_sha256": "{{{ShaOld}}}"}, "expected": {"player_table": "0x10"}}
          ]
        }
        """;

    private static readonly CompatReport _report = new(CompatResult.Hard, 2000, 0x3000, ShaNew, "300", "launch", null, ["the hook moved"]);

    private sealed class FakeSchema(string? problem = null) : ILivemapBuildsSchema
    {
        public List<(string Schema, string Builds)> Calls { get; } = [];

        public string? Check(string schemaJson, string buildsJson)
        {
            Calls.Add((schemaJson, buildsJson));
            return problem;
        }
    }

    private sealed class Scenario(string baseBuilds = BaseBuilds)
    {
        public string Base { get; } = baseBuilds;

        public JsonObject Proposed { get; } = JsonNode.Parse(baseBuilds)!.AsObject();

        public JsonArray Rows => Proposed["builds"]!.AsArray();

        public JsonObject Row => Rows[^1]!.AsObject();

        public JsonObject Sigsets => Proposed["sigsets"]!.AsObject();

        public Scenario WithNewRow()
        {
            Rows.Add(JsonNode.Parse($$$"""
                {"label": "400", "buildids": ["400"], "time_date_stamp": 2000, "size_of_image": "0x3000", "sigset": "launch", "mining_layout": "inline_array",
                 "steam": {"depot": "{{{SteamAppInfoClient.DepotId}}}", "manifest": "{{{Gid}}}", "exe_sha256": "{{{ShaNew}}}"}, "expected": {"player_table": "0x20"}}
                """));
            return this;
        }

        public string ProposedJson => Proposed.ToJsonString();

        public LivemapResult Data() => new(LivemapResultStatus.Data, "Added the row.", ProposedJson, [], null);

        public LivemapResult Code(params LivemapFile[] files) => new(LivemapResultStatus.Code, "Patched the hook.", ProposedJson, files, null);
    }

    private static string? Check(Scenario scenario, LivemapResult? result = null, FakeSchema? schema = null) =>
        new LivemapPatchValidator(schema ?? new FakeSchema()).Check(result ?? scenario.Data(), new LivemapPatchBase(scenario.Base, SchemaText, _report), BuildId, Gid);

    private static Scenario Fresh() => new Scenario().WithNewRow();

    [Fact]
    public void Check_AcceptsADataResultThatAddsOneRow()
    {
        Assert.Null(Check(Fresh()));
    }

    [Fact]
    public void Check_AcceptsOneNewSigsetThatTheNewRowUses()
    {
        var scenario = Fresh();
        scenario.Sigsets["patch2"] = scenario.Sigsets["launch"]!.DeepClone();
        scenario.Row["sigset"] = "patch2";

        Assert.Null(Check(scenario));
    }

    [Fact]
    public void Check_AcceptsAnExistingSigsetOtherThanTheBaseRows()
    {
        var scenario = Fresh();
        scenario.Row["sigset"] = "patch1";

        Assert.Null(Check(scenario));
    }

    private static readonly Dictionary<string, (Action<Scenario> Change, string Fragment)> _problems = new()
    {
        ["no row added"] = (s => s.Rows.RemoveAt(s.Rows.Count - 1), "one new row"),
        ["two rows added"] = (s => s.Rows.Add(s.Row.DeepClone()), "one new row"),
        ["new row first"] = (s => { var row = s.Rows[^1]; s.Rows.RemoveAt(s.Rows.Count - 1); s.Rows.Insert(0, row); }, "existing rows"),
        ["existing row edited"] = (s => s.Rows[0]!["mining_layout"] = "stack_record", "existing rows"),
        ["existing row replaced"] = (s => { var row = s.Rows[^1]!; s.Rows.Clear(); s.Rows.Add(row); s.Rows.Add(row.DeepClone()); }, "existing rows"),
        ["label is not the build id"] = (s => s.Row["label"] = "401", "build id"),
        ["extra build id"] = (s => s.Row["buildids"] = new JsonArray("400", "401"), "build id"),
        ["wrong build id"] = (s => s.Row["buildids"] = new JsonArray("401"), "build id"),
        ["stamp differs from the report"] = (s => s.Row["time_date_stamp"] = 2001, "fingerprint"),
        ["size differs from the report"] = (s => s.Row["size_of_image"] = "0x3001", "fingerprint"),
        ["sha differs from the report"] = (s => s.Row["steam"]!["exe_sha256"] = ShaOld, "Steam"),
        ["manifest differs"] = (s => s.Row["steam"]!["manifest"] = "445", "Steam"),
        ["manifest missing"] = (s => s.Row["steam"]!["manifest"] = null, "Steam"),
        ["depot differs"] = (s => s.Row["steam"]!["depot"] = "1", "Steam"),
        ["steam not an object"] = (s => s.Row["steam"] = "none", "Steam"),
        ["sigset not defined"] = (s => s.Row["sigset"] = "nowhere", "signature set"),
        ["sigset missing"] = (s => s.Row.Remove("sigset"), "signature set"),
        ["two sigsets added"] = (s => { s.Sigsets["a"] = s.Sigsets["launch"]!.DeepClone(); s.Sigsets["b"] = s.Sigsets["launch"]!.DeepClone(); s.Row["sigset"] = "a"; }, "signature set"),
        ["added sigset unused"] = (s => s.Sigsets["patch2"] = s.Sigsets["launch"]!.DeepClone(), "signature set"),
        ["existing sigset edited"] = (s => s.Sigsets["launch"]![0]!["offset"] = "0x1", "signature sets"),
        ["existing sigset dropped"] = (s => s.Sigsets.Remove("patch1"), "signature sets"),
        ["schema reference changed"] = (s => s.Proposed["$schema"] = "./other.json", "outside"),
        ["key added"] = (s => s.Proposed["extra"] = 1, "outside"),
    };

    public static TheoryData<string> Problems => new(_problems.Keys);

    [Theory]
    [MemberData(nameof(Problems))]
    public void Check_RejectsADataResultThatChangesMoreThanOneRowAndOneSigset(string name)
    {
        var scenario = Fresh();
        var (change, fragment) = _problems[name];
        change(scenario);

        Assert.Contains(fragment, Check(scenario));
    }

    [Fact]
    public void Check_RejectsABuildIdTheFileAlreadyLists()
    {
        var listed = BaseBuilds.Replace("\"buildids\": [\"300\"]", "\"buildids\": [\"300\", \"400\"]", StringComparison.Ordinal);
        var scenario = new Scenario(listed).WithNewRow();

        Assert.Contains("already", Check(scenario));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"sigsets":{},"sigsets":{},"builds":[]}""")]
    public void Check_RejectsBuildsThatAreNotAJsonObject(string text)
    {
        var scenario = Fresh();
        var result = new LivemapResult(LivemapResultStatus.Data, "Done.", text, [], null);

        Assert.Contains("JSON object", Check(scenario, result));
    }

    [Fact]
    public void Check_GivesTheSchemaTheBaseSchemaAndTheProposedFile()
    {
        var scenario = Fresh();
        var schema = new FakeSchema();

        Check(scenario, schema: schema);

        Assert.Equal((SchemaText, scenario.ProposedJson), Assert.Single(schema.Calls));
    }

    [Fact]
    public void Check_ReturnsWhatTheSchemaFinds()
    {
        Assert.Equal("The builds file does not satisfy the schema.", Check(Fresh(), schema: new FakeSchema("The builds file does not satisfy the schema.")));
    }

    [Theory]
    [InlineData(LivemapResultStatus.Partial)]
    [InlineData(LivemapResultStatus.GaveUp)]
    public void Check_RejectsAResultThatCarriesNoPatch(LivemapResultStatus status)
    {
        var scenario = Fresh();
        var result = new LivemapResult(status, "Not done.", null, [], "notes");

        Assert.Contains("no patch", Check(scenario, result));
    }

    [Fact]
    public void Check_RejectsFilesOnADataResult()
    {
        var scenario = Fresh();
        var result = new LivemapResult(LivemapResultStatus.Data, "Done.", scenario.ProposedJson, [new LivemapFile("src/a.cpp", "x")], null);

        Assert.Contains("code result", Check(scenario, result));
    }

    [Fact]
    public void Check_AcceptsACodeResultWithAllowedFiles()
    {
        var scenario = Fresh();

        Assert.Null(Check(scenario, scenario.Code(new LivemapFile("src/scan.cpp", "int x;"), new LivemapFile("tools/selftest.cpp", "int y;"))));
    }

    [Fact]
    public void Check_HoldsACodeResultToTheSameRowRules()
    {
        var scenario = Fresh();
        scenario.Row["label"] = "401";

        Assert.Contains("build id", Check(scenario, scenario.Code(new LivemapFile("src/scan.cpp", "int x;"))));
    }

    [Fact]
    public void Check_HoldsACodeResultToTheFileAllowlist()
    {
        var scenario = Fresh();

        Assert.Contains("allowed", Check(scenario, scenario.Code(new LivemapFile(".github/workflows/ci.yml", "on: push"))));
    }

    [Fact]
    public void Check_ChecksACodeResultAgainstTheSchemaItBrings()
    {
        var scenario = Fresh();
        var schema = new FakeSchema();
        var result = scenario.Code(new LivemapFile("data/builds.schema.json", """{"title":"new schema"}"""));

        Assert.Null(Check(scenario, result, schema));

        Assert.Equal("""{"title":"new schema"}""", Assert.Single(schema.Calls).Schema);
    }
}
