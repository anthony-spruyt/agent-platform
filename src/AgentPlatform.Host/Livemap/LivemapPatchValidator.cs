using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentPlatform.Host.Livemap;

// What a patch is judged against: the fork's builds file and schema at main, and the compat run's report of the new executable.
public sealed record LivemapPatchBase(string BuildsJson, string SchemaJson, CompatReport Report);

public sealed class LivemapPatchValidator(ILivemapBuildsSchema schema)
{
    public const string SchemaPath = "data/builds.schema.json";

    public string? Check(LivemapResult result, LivemapPatchBase baseline, long buildId, string manifestGid)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(baseline);
        if (result.Status is not (LivemapResultStatus.Data or LivemapResultStatus.Code) || result.BuildsJson is not { } proposedText)
        {
            return "The result carries no patch.";
        }

        var schemaJson = baseline.SchemaJson;
        if (result.Status == LivemapResultStatus.Data)
        {
            if (result.Files.Count > 0)
            {
                return "Only a code result carries files.";
            }
        }
        else
        {
            if (LivemapResult.CheckFiles(result.Files) is { } fileProblem)
            {
                return fileProblem;
            }

            schemaJson = result.Files.FirstOrDefault(f => f.Path == SchemaPath)?.Content ?? schemaJson;
        }

        return CheckBuilds(proposedText, baseline, buildId, manifestGid) ?? schema.Check(schemaJson, proposedText);
    }

    private static string? CheckBuilds(string proposedText, LivemapPatchBase baseline, long buildId, string manifestGid)
    {
        if (CompatReport.ParseObject(proposedText) is not { } proposed || CompatReport.ParseObject(baseline.BuildsJson) is not { } current)
        {
            return "The proposed or the current builds file is missing or not a JSON object.";
        }

        if (!OnlyRowsAndSigsetsDiffer(current, proposed))
        {
            return "The file changes something outside the builds and the signature sets.";
        }

        if (CompatClassifier.Rows(current) is not { } baseRows || CompatClassifier.Rows(proposed) is not { } rows
            || current["sigsets"] is not JsonObject baseSets || proposed["sigsets"] is not JsonObject sets)
        {
            return "The builds file needs a list of builds and an object of signature sets.";
        }

        if (rows.Count != baseRows.Count + 1)
        {
            return "The file must add one new row after the existing rows.";
        }

        if (Enumerable.Range(0, baseRows.Count).Any(i => !CompatClassifier.SameText(baseRows[i], rows[i])))
        {
            return "The existing rows must stay as they are.";
        }

        return SigsetProblem(baseSets, sets, rows[^1]!.AsObject()) ?? RowProblem(rows[^1]!.AsObject(), baseRows, baseline.Report, buildId.ToString(CultureInfo.InvariantCulture), manifestGid);
    }

    private static bool OnlyRowsAndSigsetsDiffer(JsonObject current, JsonObject proposed) =>
        current.Select(p => p.Key).SequenceEqual(proposed.Select(p => p.Key), StringComparer.Ordinal)
        && current.All(p => p.Key is "builds" or "sigsets" || CompatClassifier.SameText(p.Value, proposed[p.Key]));

    private static string? SigsetProblem(JsonObject baseSets, JsonObject sets, JsonObject row)
    {
        var kept = baseSets.Select(p => p.Key).SequenceEqual(sets.Take(baseSets.Count).Select(p => p.Key), StringComparer.Ordinal)
            && baseSets.All(p => CompatClassifier.SameText(p.Value, sets[p.Key]));
        if (!kept)
        {
            return "The existing signature sets must stay as they are.";
        }

        var added = sets.Count - baseSets.Count;
        if (added > 1)
        {
            return "The file may add one new signature set at most.";
        }

        var name = CompatReport.Text(row["sigset"]);
        if (name is null || !sets.ContainsKey(name))
        {
            return "The sigset of the new row is not one of the signature sets.";
        }

        return added == 1 && sets.Last().Key != name ? "The new signature set must be the one the new row uses." : null;
    }

    private static string? RowProblem(JsonObject row, JsonArray baseRows, CompatReport report, string id, string manifestGid)
    {
        if (CompatClassifier.Lists(baseRows, id))
        {
            return "The build id is already in the builds file.";
        }

        if (CompatClassifier.Label(row) != id || CompatClassifier.Ids(row) is not { } ids || !ids.SequenceEqual([id]))
        {
            return "The new row must be labelled with the build id and list that build id alone.";
        }

        if (!CompatClassifier.HasFingerprint(row, report))
        {
            return "The new row does not carry the fingerprint of the executable.";
        }

        return row["steam"] is JsonObject steam
            && CompatReport.Text(steam["depot"]) == SteamAppInfoClient.DepotId
            && CompatReport.Text(steam["manifest"]) == manifestGid
            && CompatReport.Text(steam["exe_sha256"]) == report.Sha256
                ? null
                : "The new row does not record the depot, manifest and checksum of the build in its Steam data.";
    }
}
