using System.Text.Json.Nodes;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

public class CompatClassifierTests
{
    private const long BuildId = 400;
    private const string Gid = "444";
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ShaNew = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private const string ForkBuilds = """
        {
          "$schema": "builds.schema.json",
          "sigsets": { "s1": [ { "site": "a", "bytes": "01 02", "offset": "0x0", "mandatory": true } ], "s2": [ { "site": "a", "bytes": "03 04", "offset": "0x1", "mandatory": true } ] },
          "builds": [
            { "label": "100", "buildids": ["100"], "time_date_stamp": 1000, "size_of_image": "0x1000", "sigset": "s1", "mining_layout": "inline_array",
              "steam": { "depot": null, "manifest": null, "exe_sha256": null }, "expected": { "site_a": "0x10", "site_b": "0x20" } },
            { "label": "300", "buildids": ["300", "301"], "time_date_stamp": 2000, "size_of_image": "0x2000", "sigset": "s2", "mining_layout": "stack_record",
              "steam": { "depot": "2278521", "manifest": "333", "exe_sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
              "expected": { "site_a": "0x11", "site_b": "0x21" }, "expected_operands": { "entity_table": "0x830" } }
          ]
        }
        """;

    private sealed class Scenario
    {
        public JsonObject Fork { get; } = JsonNode.Parse(ForkBuilds)!.AsObject();

        public JsonObject Artifact { get; set; } = JsonNode.Parse(ForkBuilds)!.AsObject();

        public JsonObject Report { get; set; } = new();

        public bool WithArtifactBuilds { get; set; } = true;

        public bool WithForkBuilds { get; set; } = true;

        public string? ReportText { get; set; }

        public JsonArray ForkRows => Fork["builds"]!.AsArray();

        public JsonArray Rows => Artifact["builds"]!.AsArray();

        public JsonObject Row(int index) => Rows[index]!.AsObject();

        public CompatOutcome Run() => CompatClassifier.Classify(
            new CompatArtifact(ReportText ?? Report.ToJsonString(), WithArtifactBuilds ? Artifact.ToJsonString() : null),
            WithForkBuilds ? Fork.ToJsonString() : null,
            BuildId,
            Gid);

        public static Scenario Alias()
        {
            var scenario = new Scenario();
            var row = scenario.Row(1);
            row["buildids"]!.AsArray().Add("400");
            row["steam"]!["manifest"] = Gid;
            scenario.Report = ReportFor("known", 2000, "0x2000", ShaC, "300", "s2", matched: "300");
            return scenario;
        }

        public static Scenario AliasOfTheRowWithoutSteamData()
        {
            var scenario = new Scenario();
            var row = scenario.Row(0);
            row["buildids"]!.AsArray().Add("400");
            row["steam"]!["manifest"] = Gid;
            scenario.Report = ReportFor("known", 1000, "0x1000", ShaA, "300", "s2", matched: "100");
            return scenario;
        }

        public static Scenario NewRow()
        {
            var scenario = new Scenario();
            var row = scenario.Row(1).DeepClone().AsObject();
            row["label"] = "400";
            row["buildids"] = new JsonArray("400");
            row["time_date_stamp"] = 3000;
            row["size_of_image"] = "0x3000";
            row["steam"] = new JsonObject { ["depot"] = "2278521", ["manifest"] = Gid, ["exe_sha256"] = ShaNew };
            row["expected"] = new JsonObject { ["site_a"] = "0x12", ["site_b"] = "0x22" };
            row["expected_operands"] = new JsonObject { ["entity_table"] = "0x830" };
            scenario.Rows.Add(row);
            scenario.Report = ReportFor("easy", 3000, "0x3000", ShaNew, "300", "s2", matched: null);
            return scenario;
        }

        public static JsonObject ReportFor(string result, long stamp, string size, string sha, string baseLabel, string baseSigset, string? matched) => new()
        {
            ["result"] = result,
            ["build_id"] = BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["manifest"] = Gid,
            ["exe"] = new JsonObject { ["time_date_stamp"] = stamp, ["size_of_image"] = size, ["sha256"] = sha },
            ["base"] = new JsonObject { ["label"] = baseLabel, ["sigset"] = baseSigset },
            ["matched_row"] = matched,
            ["reasons"] = new JsonArray(),
        };
    }

    [Fact]
    public void AnAlias_OnARowThatIsAlreadyKnown_IsKnown()
    {
        var outcome = Scenario.Alias().Run();

        Assert.Equal(CompatOutcomeKind.Known, outcome.Kind);
        Assert.Equal("300", outcome.RowLabel);
    }

    [Fact]
    public void AnAlias_OnARowThatHasNoSteamData_IsKnownByItsFingerprint()
    {
        var outcome = Scenario.AliasOfTheRowWithoutSteamData().Run();

        Assert.Equal(CompatOutcomeKind.Known, outcome.Kind);
        Assert.Equal("100", outcome.RowLabel);
    }

    [Fact]
    public void ANewRowAtTheEnd_IsEasy()
    {
        var outcome = Scenario.NewRow().Run();

        Assert.Equal(CompatOutcomeKind.Easy, outcome.Kind);
        Assert.Equal("400", outcome.RowLabel);
    }

    [Fact]
    public void AHardResult_NeedsNoBuilds_AndKeepsItsReasons()
    {
        var scenario = new Scenario { WithArtifactBuilds = false, WithForkBuilds = false };
        scenario.Report = Scenario.ReportFor("hard", 3000, "0x3000", ShaNew, "300", "s2", matched: null);
        scenario.Report["reasons"] = new JsonArray("site movement matched 0 times", "a branch lands inside the hook");

        var outcome = scenario.Run();

        Assert.Equal(CompatOutcomeKind.Hard, outcome.Kind);
        Assert.Contains("site movement matched 0 times", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("a branch lands inside the hook", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AHardResult_KeepsOnlyAFewShortReasons()
    {
        var scenario = new Scenario { WithArtifactBuilds = false };
        scenario.Report = Scenario.ReportFor("hard", 3000, "0x3000", ShaNew, "300", "s2", matched: null);
        scenario.Report["reasons"] = new JsonArray(Enumerable.Range(0, 50).Select(i => (JsonNode)JsonValue.Create(new string((char)('a' + (i % 26)), 5000))!).ToArray());

        var outcome = scenario.Run();

        Assert.Equal(CompatOutcomeKind.Hard, outcome.Kind);
        Assert.InRange(outcome.Detail.Length, 1, 1500);
    }

    private static readonly Dictionary<string, Action<Scenario>> _reportProblems = new()
    {
        ["not-an-object"] = s => s.ReportText = "[]",
        ["not-json"] = s => s.ReportText = "result: known",
        ["empty"] = s => s.ReportText = string.Empty,
        ["no-result"] = s => s.Report.Remove("result"),
        ["unknown-result"] = s => s.Report["result"] = "trivial",
        ["result-in-another-case"] = s => s.Report["result"] = "Known",
        ["wrong-build"] = s => s.Report["build_id"] = "401",
        ["build-as-number"] = s => s.Report["build_id"] = 400,
        ["wrong-manifest"] = s => s.Report["manifest"] = "445",
        ["no-exe"] = s => s.Report.Remove("exe"),
        ["bad-checksum"] = s => s.Report["exe"]!["sha256"] = "ABC",
        ["uppercase-checksum"] = s => s.Report["exe"]!["sha256"] = ShaC.ToUpperInvariant(),
        ["stamp-as-text"] = s => s.Report["exe"]!["time_date_stamp"] = "2000",
        ["size-not-hex"] = s => s.Report["exe"]!["size_of_image"] = "big",
        ["no-base"] = s => s.Report.Remove("base"),
        ["no-base-label"] = s => s.Report["base"]!["label"] = string.Empty,
        ["no-base-sigset"] = s => s.Report["base"]!["sigset"] = string.Empty,
        ["checksum-with-a-line-break"] = s => s.Report["exe"]!["sha256"] = ShaNew + "\n",
        ["size-with-a-line-break"] = s => s.Report["exe"]!["size_of_image"] = "0x3000\n",
        ["size-past-the-signed-range"] = s => s.Report["exe"]!["size_of_image"] = "0xffffffffffffffff",
        ["a-build-named-twice"] = s => s.ReportText = s.Report.ToJsonString().Replace("{", "{\"build_id\":\"399\",", StringComparison.Ordinal),
        ["nested-too-deep"] = s => s.ReportText = s.Report.ToJsonString().Replace("{", "{\"deep\":" + new string('[', 40) + new string(']', 40) + ",", StringComparison.Ordinal),
    };

    public static TheoryData<string> ReportProblems => new(_reportProblems.Keys);

    [Theory]
    [MemberData(nameof(ReportProblems))]
    public void AReportThatIsNotWhatWasAsked_IsInvalid(string problem)
    {
        var scenario = new Scenario { WithArtifactBuilds = false };
        scenario.Report = Scenario.ReportFor("hard", 3000, "0x3000", ShaNew, "300", "s2", matched: null);
        _reportProblems[problem](scenario);

        Assert.Equal(CompatOutcomeKind.Invalid, scenario.Run().Kind);
    }

    private static readonly Dictionary<string, Action<Scenario>> _aliasProblems = new()
    {
        ["no-builds-file"] = s => s.WithArtifactBuilds = false,
        ["no-fork-file"] = s => s.WithForkBuilds = false,
        ["nothing-changed"] = s => s.Artifact = JsonNode.Parse(ForkBuilds)!.AsObject(),
        ["two-rows-changed"] = s => s.Row(0)["buildids"]!.AsArray().Add("400"),
        ["another-field-changed"] = s => s.Row(1)["mining_layout"] = "inline_array",
        ["a-site-changed"] = s => s.Row(1)["expected"]!["site_a"] = "0x99",
        ["the-checksum-changed"] = s => s.Row(1)["steam"]!["exe_sha256"] = ShaNew,
        ["the-depot-changed"] = s => s.Row(1)["steam"]!["depot"] = "1",
        ["the-label-changed"] = s => s.Row(1)["label"] = "400",
        ["the-id-comes-first"] = s => s.Row(1)["buildids"] = new JsonArray("400", "300", "301"),
        ["the-ids-are-replaced"] = s => s.Row(1)["buildids"] = new JsonArray("400"),
        ["an-id-is-dropped"] = s => s.Row(1)["buildids"] = new JsonArray("300", "400"),
        ["another-id-is-added"] = s => s.Row(1)["buildids"] = new JsonArray("300", "301", "400", "401"),
        ["the-id-is-another-build"] = s => s.Row(1)["buildids"] = new JsonArray("300", "301", "401"),
        ["the-id-is-the-label-of-another-row"] = s =>
        {
            s.ForkRows[0]!["label"] = "400";
            s.Row(0)["label"] = "400";
        },
        ["an-id-is-a-number"] = s =>
        {
            s.ForkRows[1]!["buildids"] = new JsonArray("300", 301);
            s.Row(1)["buildids"] = new JsonArray("300", 301, "400");
        },
        ["the-manifest-is-not-the-new-one"] = s => s.Row(1)["steam"]!["manifest"] = "333",
        ["the-manifest-is-another"] = s => s.Row(1)["steam"]!["manifest"] = "445",
        ["a-sigset-changed"] = s => s.Artifact["sigsets"]!["s2"] = new JsonArray(),
        ["a-top-level-key-added"] = s => s.Artifact["extra"] = true,
        ["the-schema-pointer-changed"] = s => s.Artifact["$schema"] = "elsewhere.json",
        ["the-fingerprint-differs-in-the-stamp"] = s => s.Report["exe"]!["time_date_stamp"] = 2001,
        ["the-fingerprint-differs-in-the-size"] = s => s.Report["exe"]!["size_of_image"] = "0x2001",
        ["the-checksum-differs-from-the-row"] = s => s.Report["exe"]!["sha256"] = ShaNew,
        ["the-matched-row-is-another"] = s => s.Report["matched_row"] = "100",
        ["no-matched-row"] = s => s.Report["matched_row"] = null,
        ["it-says-easy"] = s => s.Report["result"] = "easy",
        ["a-row-removed-and-one-aliased"] = s => s.Rows.RemoveAt(0),
    };

    public static TheoryData<string> AliasProblems => new(_aliasProblems.Keys);

    [Theory]
    [MemberData(nameof(AliasProblems))]
    public void AnAliasThatDoesMoreOrOtherThanAddTheId_IsInvalid(string problem)
    {
        var scenario = Scenario.Alias();
        _aliasProblems[problem](scenario);

        Assert.Equal(CompatOutcomeKind.Invalid, scenario.Run().Kind);
    }

    [Fact]
    public void AnAlias_WithAnotherRowChangedAfterIt_IsInvalid()
    {
        var scenario = Scenario.AliasOfTheRowWithoutSteamData();
        scenario.Row(1)["mining_layout"] = "inline_array";

        Assert.Equal(CompatOutcomeKind.Invalid, scenario.Run().Kind);
    }

    [Fact]
    public void AnAlias_ForAnIdThatTheForkListsAlready_IsInvalid()
    {
        var scenario = Scenario.Alias();
        scenario.ForkRows[0]!["buildids"]!.AsArray().Add("400");
        scenario.Row(0)["buildids"]!.AsArray().Add("400");

        Assert.Equal(CompatOutcomeKind.Invalid, scenario.Run().Kind);
    }

    private static readonly Dictionary<string, Action<Scenario>> _newRowProblems = new()
    {
        ["no-builds-file"] = s => s.WithArtifactBuilds = false,
        ["no-fork-file"] = s => s.WithForkBuilds = false,
        ["an-old-row-changed"] = s => s.Row(0)["mining_layout"] = "stack_record",
        ["an-old-row-got-the-id"] = s => s.Row(0)["buildids"]!.AsArray().Add("400"),
        ["the-new-row-is-a-number"] = s =>
        {
            s.Rows.RemoveAt(2);
            s.Rows.Add(5);
        },
        ["the-new-row-is-not-last"] = s =>
        {
            var row = s.Rows[2]!;
            s.Rows.RemoveAt(2);
            s.Rows.Insert(0, row);
        },
        ["two-rows-added"] = s => s.Rows.Add(s.Row(2).DeepClone()),
        ["a-sigset-changed"] = s => s.Artifact["sigsets"]!["s1"] = new JsonArray(),
        ["a-sigset-added"] = s => s.Artifact["sigsets"]!.AsObject()["s3"] = new JsonArray(),
        ["the-label-is-not-the-id"] = s => s.Row(2)["label"] = "401",
        ["the-label-is-taken"] = s => s.Row(2)["label"] = "300",
        ["more-than-one-id"] = s => s.Row(2)["buildids"] = new JsonArray("400", "401"),
        ["the-id-is-another"] = s => s.Row(2)["buildids"] = new JsonArray("401"),
        ["the-id-is-listed-already"] = s =>
        {
            s.ForkRows[0]!["buildids"]!.AsArray().Add("400");
            s.Row(0)["buildids"]!.AsArray().Add("400");
        },
        ["the-depot-is-missing"] = s => s.Row(2)["steam"]!["depot"] = null,
        ["the-depot-is-another"] = s => s.Row(2)["steam"]!["depot"] = "2278522",
        ["the-manifest-is-another"] = s => s.Row(2)["steam"]!["manifest"] = "445",
        ["the-manifest-is-missing"] = s => s.Row(2)["steam"]!["manifest"] = null,
        ["the-checksum-is-another"] = s => s.Row(2)["steam"]!["exe_sha256"] = ShaC,
        ["the-checksum-is-missing"] = s => s.Row(2)["steam"]!["exe_sha256"] = null,
        ["the-stamp-is-another"] = s => s.Row(2)["time_date_stamp"] = 3001,
        ["the-size-is-another"] = s => s.Row(2)["size_of_image"] = "0x3001",
        ["the-sigset-is-not-the-bases"] = s => s.Row(2)["sigset"] = "s1",
        ["the-layout-is-not-the-bases"] = s => s.Row(2)["mining_layout"] = "inline_array",
        ["a-site-is-missing"] = s => s.Row(2)["expected"]!.AsObject().Remove("site_b"),
        ["a-site-is-added"] = s => s.Row(2)["expected"]!.AsObject()["site_c"] = "0x30",
        ["a-site-is-not-hex"] = s => s.Row(2)["expected"]!["site_a"] = "ten",
        ["a-site-is-a-number"] = s => s.Row(2)["expected"]!["site_a"] = 18,
        ["the-operands-are-missing"] = s => s.Row(2).Remove("expected_operands"),
        ["an-operand-is-not-hex"] = s => s.Row(2)["expected_operands"]!["entity_table"] = "next",
        ["a-key-is-added-to-the-row"] = s => s.Row(2)["note"] = "x",
        ["a-key-is-added-to-the-steam-data"] = s => s.Row(2)["steam"]!["extra"] = "x",
        ["the-base-is-unknown"] = s => s.Report["base"]!["label"] = "999",
        ["the-base-sigset-is-another"] = s => s.Report["base"]!["sigset"] = "s1",
        ["the-report-names-another-sigset-for-the-base"] = s =>
        {
            s.Row(2)["sigset"] = "s1";
            s.Report["base"]!["sigset"] = "s1";
        },
        ["the-report-checksum-is-another"] = s => s.Report["exe"]!["sha256"] = ShaC,
        ["it-says-known"] = s => s.Report["result"] = "known",
    };

    public static TheoryData<string> NewRowProblems => new(_newRowProblems.Keys);

    [Theory]
    [MemberData(nameof(NewRowProblems))]
    public void ANewRowThatIsNotExactlyWhatTheReportDescribes_IsInvalid(string problem)
    {
        var scenario = Scenario.NewRow();
        _newRowProblems[problem](scenario);

        var outcome = scenario.Run();

        Assert.Equal(CompatOutcomeKind.Invalid, outcome.Kind);
        Assert.NotEmpty(outcome.Detail);
    }

    [Fact]
    public void ANewRow_BasedOnARowThatIsNotTheNewestOne_IsInvalid()
    {
        var scenario = Scenario.NewRow();
        scenario.Report["base"]!["label"] = "100";
        scenario.Report["base"]!["sigset"] = "s1";
        scenario.Row(2)["sigset"] = "s1";
        scenario.Row(2)["mining_layout"] = "inline_array";
        scenario.Row(2).Remove("expected_operands");

        var outcome = scenario.Run();

        Assert.Equal(CompatOutcomeKind.Invalid, outcome.Kind);
        Assert.Contains("after the one it is based on", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowTheSizeOfWhichIsWrittenWithAnotherCase_IsStillTheSameSize()
    {
        var scenario = Scenario.NewRow();
        scenario.Report["exe"]!["size_of_image"] = "0x3000";
        scenario.Row(2)["size_of_image"] = "0x03000";

        Assert.Equal(CompatOutcomeKind.Easy, scenario.Run().Kind);
    }
}
