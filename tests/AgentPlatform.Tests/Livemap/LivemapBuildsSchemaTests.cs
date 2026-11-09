using System.Text.Json.Nodes;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class LivemapBuildsSchemaTests
{
    private static readonly LivemapBuildsSchemaValidator _validator = new();

    private static JsonObject ValidFile() => JsonNode.Parse("""
        {
          "$schema": "builds.schema.json",
          "sigsets": {
            "v1": [
              { "site": "player_table", "bytes": "48 8b ?? 01", "offset": "0x0", "mandatory": true },
              { "site": "movement", "bytes": "48 8b ?? 02", "offset": "0x1", "mandatory": true },
              { "site": "daytime", "bytes": "48 8b ?? 03", "offset": "0x2", "mandatory": true },
              { "site": "registry", "bytes": "48 8b ?? 04", "offset": "0x3", "mandatory": true }
            ]
          },
          "builds": [
            {
              "label": "26404334",
              "buildids": ["26404334"],
              "time_date_stamp": 1730000000,
              "size_of_image": "0x1dde000",
              "sigset": "v1",
              "mining_layout": "inline_array",
              "steam": { "depot": "2278521", "manifest": "7782830484557676320", "exe_sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
              "expected": { "player_table": "0x10", "movement": "0x20", "daytime": "0x30", "registry": "0x40" }
            }
          ]
        }
        """)!.AsObject();

    private static string? Check(JsonObject file) => _validator.Check(BuildsSchemaFixture.Schema, file.ToJsonString());

    [Fact]
    public void AFileThatFollowsTheSchema_HasNoProblem() =>
        Assert.Null(Check(ValidFile()));

    [Fact]
    public void AFieldTheSchemaDoesNotKnow_IsAProblem()
    {
        var file = ValidFile();
        file["builds"]![0]!["note"] = "extra";

        Assert.NotNull(Check(file));
    }

    [Fact]
    public void ASigsetWithoutAMandatoryHookSite_IsAProblem()
    {
        var file = ValidFile();
        file["sigsets"]!["v1"]!.AsArray().RemoveAt(1);

        Assert.NotNull(Check(file));
    }

    [Fact]
    public void AnOffsetWithLeadingZeros_IsAProblem()
    {
        var file = ValidFile();
        file["builds"]![0]!["expected"]!["movement"] = "0x0020";

        Assert.NotNull(Check(file));
    }

    [Fact]
    public void ABuildThatExpectsTheJoinLineSite_IsAProblem()
    {
        var file = ValidFile();
        file["builds"]![0]!["expected"]!["join_line"] = "0x50";

        Assert.NotNull(Check(file));
    }

    [Fact]
    public void ASchemaThatReachesForAnotherDocument_IsAProblemInsteadOfAFetch()
    {
        var problem = _validator.Check("""{ "$ref": "https://example.invalid/other.json" }""", "{}");

        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("not json", "{}")]
    [InlineData("""{ "type": "object" }""", "not json")]
    public void TextThatIsNotJson_IsAProblem(string schema, string file) =>
        Assert.NotNull(_validator.Check(schema, file));

    [Fact]
    public void AProblem_NamesWhereTheFileBreaksTheSchema_InFewChars()
    {
        var file = ValidFile();
        file["builds"]![0]!["mining_layout"] = "other";

        var problem = Check(file);

        Assert.NotNull(problem);
        Assert.Contains("mining_layout", problem, StringComparison.Ordinal);
        Assert.True(problem.Length <= LivemapBuildsSchemaValidator.MaxProblemChars);
    }
}
