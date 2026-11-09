using System.Globalization;
using System.Text.Json.Nodes;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Temporalio.Testing;

namespace AgentPlatform.Tests.Livemap;

internal sealed class LivemapBuildRig
{
    public const long BuildId = 400;
    public const string Gid = "444";
    public const string Sha = "1111111111111111111111111111111111111111";
    public const string ExeSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public const string NewExeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    public const string ForkBuilds = """
        {
          "$schema": "builds.schema.json",
          "sigsets": { "s1": [ { "site": "a", "bytes": "01 02", "offset": "0x0", "mandatory": true } ] },
          "builds": [
            { "label": "300", "buildids": ["300"], "time_date_stamp": 2000, "size_of_image": "0x2000", "sigset": "s1", "mining_layout": "stack_record",
              "steam": { "depot": "2278521", "manifest": "333", "exe_sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
              "expected": { "site_a": "0x11" } }
          ]
        }
        """;

    public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset SteamUpdated = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const long ExeCaptureSuiteId = 8001;
    public const long CompatSuiteId = 8002;

    public static long SuiteOf(long runId) => runId == FakeLivemapGitHub.ExeCaptureRunId ? ExeCaptureSuiteId : CompatSuiteId;

    public static LivemapRun Finished(long id, string conclusion = "success") => new(id, "completed", conclusion, Sha, SuiteOf(id));

    public static LivemapRun Running(long id) => new(id, "in_progress", null, Sha, SuiteOf(id));

    public static string ReportJson(string result, string exeSha, long stamp, string size, string? matched, params string[] reasons)
    {
        var report = new JsonObject
        {
            ["result"] = result,
            ["build_id"] = BuildId.ToString(CultureInfo.InvariantCulture),
            ["manifest"] = Gid,
            ["exe"] = new JsonObject { ["time_date_stamp"] = stamp, ["size_of_image"] = size, ["sha256"] = exeSha },
            ["base"] = new JsonObject { ["label"] = "300", ["sigset"] = "s1" },
            ["matched_row"] = matched,
            ["reasons"] = new JsonArray(reasons.Select(r => JsonValue.Create(r)).ToArray<JsonNode?>()),
        };
        return report.ToJsonString();
    }

    public static CompatArtifact KnownArtifact()
    {
        var builds = JsonNode.Parse(ForkBuilds)!.AsObject();
        var row = builds["builds"]!.AsArray()[0]!.AsObject();
        row["buildids"]!.AsArray().Add("400");
        row["steam"]!["manifest"] = Gid;
        return new CompatArtifact(ReportJson("known", ExeSha, 2000, "0x2000", "300"), builds.ToJsonString());
    }

    public static CompatArtifact EasyArtifact()
    {
        var builds = JsonNode.Parse(ForkBuilds)!.AsObject();
        var row = builds["builds"]!.AsArray()[0]!.DeepClone().AsObject();
        row["label"] = "400";
        row["buildids"] = new JsonArray("400");
        row["time_date_stamp"] = 3000;
        row["size_of_image"] = "0x3000";
        row["steam"] = new JsonObject { ["depot"] = "2278521", ["manifest"] = Gid, ["exe_sha256"] = NewExeSha };
        row["expected"] = new JsonObject { ["site_a"] = "0x12" };
        builds["builds"]!.AsArray().Add(row);
        return new CompatArtifact(ReportJson("easy", NewExeSha, 3000, "0x3000", null), builds.ToJsonString());
    }

    public static CompatArtifact HardArtifact() =>
        new(ReportJson("hard", NewExeSha, 3000, "0x3000", null, "site movement matched 0 times"), null);

    public LivemapBuildRig()
    {
        Data = InMemoryRunData.Create();
        Store = new LivemapBuildStore(Data.Factory, new FixedTimeProvider(Now));
        Activities = new LivemapBuildActivities(GitHub, Store);
        GitHub.ForkBuilds = ForkBuilds;
    }

    public InMemoryRunData Data { get; }

    public LivemapBuildStore Store { get; }

    public FakeLivemapGitHub GitHub { get; } = new();

    public LivemapBuildActivities Activities { get; }

    public Task<LivemapBuild> SeedAsync(LivemapBuildState state = LivemapBuildState.Detected) =>
        Store.RecordAsync(BuildId, Gid, SteamUpdated, state, Ct);

    public async Task SetAttemptsAsync(int attempts)
    {
        await using var db = await Data.Factory.CreateDbContextAsync(Ct);
        var row = await db.LivemapBuilds.SingleAsync(b => b.BuildId == BuildId, Ct);
        row.Attempts = attempts;
        await db.SaveChangesAsync(Ct);
    }

    public async Task<LivemapBuild> RowAsync() => (await Store.FindAsync(BuildId, Ct))!;

    public Task<LivemapBuildOutcome> ClassifyAsync() =>
        new ActivityEnvironment().RunAsync(() => Activities.ClassifyAsync(new LivemapClassifyInput(BuildId, Gid, FakeLivemapGitHub.CompatRunId)));
}
