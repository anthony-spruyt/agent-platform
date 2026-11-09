using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentPlatform.Host.Livemap;

public enum CompatOutcomeKind
{
    Known,
    Easy,
    Hard,
    Invalid,
}

public sealed record CompatOutcome(CompatOutcomeKind Kind, string Detail, string? RowLabel = null)
{
    public static CompatOutcome Invalid(string detail) => new(CompatOutcomeKind.Invalid, detail);
}

// The compat run is an input, not a verdict: the report and the proposed builds file are checked against the fork's own file before anything acts on them.
public static class CompatClassifier
{
    private const int MaxReasons = 5;

    private const int MaxReasonChars = 200;

    private static readonly string[] _steamKeys = ["depot", "manifest", "exe_sha256"];

    public static CompatOutcome Classify(CompatArtifact artifact, string? forkBuildsJson, long buildId, string manifestGid)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!CompatReport.TryParse(artifact.ReportJson, buildId, manifestGid, out var parsed, out var problem))
        {
            return CompatOutcome.Invalid(problem);
        }

        var report = parsed!;
        if (report.Result == CompatResult.Hard)
        {
            return new CompatOutcome(CompatOutcomeKind.Hard, Summarize(report.Reasons));
        }

        if (CompatReport.ParseObject(artifact.BuildsJson) is not { } proposed || CompatReport.ParseObject(forkBuildsJson) is not { } fork)
        {
            return CompatOutcome.Invalid("The proposed or the current builds file is missing or not a JSON object.");
        }

        if (Rows(proposed) is not { } proposedRows || Rows(fork) is not { } forkRows || !OnlyRowsDiffer(fork, proposed))
        {
            return CompatOutcome.Invalid("The proposed builds file changes more than its rows.");
        }

        var id = buildId.ToString(CultureInfo.InvariantCulture);
        var change = new Change(forkRows, proposedRows, report, id, manifestGid);
        return proposedRows.Count == forkRows.Count
            ? change.CheckAlias()
            : proposedRows.Count == forkRows.Count + 1
                ? change.CheckNewRow()
                : CompatOutcome.Invalid("The proposed builds file adds or drops more than one row.");
    }

    private static string Summarize(IReadOnlyList<string> reasons) =>
        reasons.Count == 0
            ? "The compat run found the patch hard."
            : string.Join("; ", reasons.Take(MaxReasons).Select(reason => reason.Length > MaxReasonChars ? reason[..MaxReasonChars] : reason));

    private static JsonArray? Rows(JsonObject document) =>
        document["builds"] is JsonArray rows && rows.All(row => row is JsonObject) ? rows : null;

    // JsonNode.DeepEquals ignores key order and number spelling, so anything the fork's file must keep is compared as text.
    private static bool SameText(JsonNode? first, JsonNode? second) =>
        string.Equals(first?.ToJsonString(), second?.ToJsonString(), StringComparison.Ordinal);

    private static bool OnlyRowsDiffer(JsonObject fork, JsonObject proposed) =>
        fork.Select(p => p.Key).SequenceEqual(proposed.Select(p => p.Key), StringComparer.Ordinal)
        && fork.All(p => p.Key == "builds" || SameText(p.Value, proposed[p.Key]));

    private static List<string>? Ids(JsonObject row) =>
        row["buildids"] is JsonArray ids && ids.All(id => CompatReport.Text(id) is not null) ? [.. ids.Select(id => CompatReport.Text(id)!)] : null;

    private static string? Label(JsonObject row) => CompatReport.Text(row["label"]);

    private static bool Lists(JsonArray rows, string id) =>
        rows.Any(row => Label(row!.AsObject()) == id || Ids(row.AsObject())?.Contains(id) == true);

    private static bool SameKeys(JsonObject first, JsonObject second) =>
        first.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(second.Select(p => p.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool HasFingerprint(JsonObject row, CompatReport report) =>
        row["time_date_stamp"] is JsonValue stamp
        && stamp.TryGetValue<long>(out var stampValue)
        && stampValue == report.TimeDateStamp
        && CompatReport.TryParseHex(CompatReport.Text(row["size_of_image"]), out var size)
        && size == report.SizeOfImage;

    private static bool IsHexMap(JsonNode? node, JsonNode? model) =>
        node is JsonObject map
        && model is JsonObject modelMap
        && SameKeys(map, modelMap)
        && map.All(p => CompatReport.TryParseHex(CompatReport.Text(p.Value), out _));

    private sealed class Change(JsonArray forkRows, JsonArray proposedRows, CompatReport report, string id, string manifestGid)
    {
        public CompatOutcome CheckAlias()
        {
            if (report.Result != CompatResult.Known)
            {
                return CompatOutcome.Invalid("The report does not describe a known build, but the builds file only gains an alias.");
            }

            var changed = Enumerable.Range(0, forkRows.Count).Where(i => !SameText(forkRows[i], proposedRows[i])).ToList();
            if (changed.Count != 1)
            {
                return CompatOutcome.Invalid($"A known build changes one row, but {changed.Count} rows changed.");
            }

            var before = forkRows[changed[0]]!.AsObject();
            var after = proposedRows[changed[0]]!.AsObject();
            var problem = AliasProblem(before, after);
            return problem is null ? new CompatOutcome(CompatOutcomeKind.Known, "The build is an alias of a known row.", Label(after)) : CompatOutcome.Invalid(problem);
        }

        public CompatOutcome CheckNewRow()
        {
            if (report.Result != CompatResult.Easy)
            {
                return CompatOutcome.Invalid("The report does not describe an easy build, but the builds file gains a row.");
            }

            var alteredRows = Enumerable.Range(0, forkRows.Count).Count(i => !SameText(forkRows[i], proposedRows[i]));
            var models = forkRows.Select(row => row!.AsObject()).Where(row => Label(row) == report.BaseLabel).ToList();
            if (alteredRows > 0 || models.Count != 1 || !ReferenceEquals(models[0], forkRows[^1]))
            {
                return CompatOutcome.Invalid("An easy build only appends a row after the one it is based on and leaves every other row alone.");
            }

            var added = proposedRows[^1]!.AsObject();
            var problem = Lists(forkRows, id) ? "The build id is already in the builds file." : NewRowProblem(added, models[0]);
            return problem is null ? new CompatOutcome(CompatOutcomeKind.Easy, "The build needs a new row.", Label(added)) : CompatOutcome.Invalid(problem);
        }

        private string? AliasProblem(JsonObject before, JsonObject after)
        {
            if (before["steam"] is not JsonObject || after["steam"] is not JsonObject steamAfter)
            {
                return "The row has no Steam data.";
            }

            var idsBefore = Ids(before);
            var idsAfter = Ids(after);
            if (!SameText(WithNewIdsAndManifest(before, after), after))
            {
                return "The alias changes more than the build ids and the manifest.";
            }

            if (idsBefore is null || idsAfter is null || !idsAfter.SequenceEqual(idsBefore.Append(id)) || Lists(forkRows, id))
            {
                return "The alias must add the new build id, once, after the ones the row lists.";
            }

            if (CompatReport.Text(steamAfter["manifest"]) != manifestGid)
            {
                return "The alias does not record the new manifest.";
            }

            var sha = CompatReport.Text(steamAfter["exe_sha256"]);
            return !HasFingerprint(after, report) || (sha is not null && sha != report.Sha256) || report.MatchedRow != Label(after)
                ? "The row is not the one the executable fingerprint matches."
                : null;
        }

        private string? NewRowProblem(JsonObject row, JsonObject model)
        {
            if (!SameKeys(row, model) || Label(row) != id || Ids(row) is not { } ids || !ids.SequenceEqual([id]) || !HasFingerprint(row, report))
            {
                return "The new row does not carry the build id and the fingerprint of the executable.";
            }

            if (CompatReport.Text(row["sigset"]) != report.BaseSigset || CompatReport.Text(model["sigset"]) != report.BaseSigset || !JsonNode.DeepEquals(row["mining_layout"], model["mining_layout"]))
            {
                return "The new row does not copy the signature set and layout of the row it is based on.";
            }

            return SteamProblem(row["steam"], model["steam"]) ?? ExpectedProblem(row, model);
        }

        private string? SteamProblem(JsonNode? steam, JsonNode? model) =>
            steam is JsonObject data
            && model is JsonObject modelData
            && SameKeys(data, modelData)
            && _steamKeys.All(key => data.ContainsKey(key))
            && CompatReport.Text(data["depot"]) == SteamAppInfoClient.DepotId
            && CompatReport.Text(data["manifest"]) == manifestGid
            && CompatReport.Text(data["exe_sha256"]) == report.Sha256
                ? null
                : "The new row does not record the depot, manifest and checksum of the build.";

        private static string? ExpectedProblem(JsonObject row, JsonObject model)
        {
            var hasOperands = model.ContainsKey("expected_operands");
            return IsHexMap(row["expected"], model["expected"]) && (!hasOperands || IsHexMap(row["expected_operands"], model["expected_operands"]))
                ? null
                : "The new row does not hold one hex offset for each site the row it is based on has.";
        }

        private static JsonObject WithNewIdsAndManifest(JsonObject before, JsonObject after)
        {
            var copy = before.DeepClone().AsObject();
            copy["buildids"] = after["buildids"]?.DeepClone();
            copy["steam"]!.AsObject()["manifest"] = after["steam"]!["manifest"]?.DeepClone();
            return copy;
        }
    }
}
