using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Client;

namespace AgentPlatform.Tests.Replay;

internal static partial class HistoryStore
{
    private const string RecordVariable = "RECORD_HISTORIES";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string SourceDirectory([CallerFilePath] string callerFile = "") =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "Histories");

    public static IEnumerable<string> Names()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Replay", "Histories");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)
            : [];
    }

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Replay", "Histories", $"{name}.json"));

    public static async Task RecordAsync(WorkflowHandle handle, string name)
    {
        if (Environment.GetEnvironmentVariable(RecordVariable) != "1")
        {
            return;
        }

        var history = await handle.FetchHistoryAsync();
        var json = IdentityPattern().Replace(history.ToJson(), "\"identity\": \"recorder\"");
        using var document = JsonDocument.Parse(json);
        await File.WriteAllTextAsync(
            Path.Combine(SourceDirectory(), $"{name}.json"),
            JsonSerializer.Serialize(document, Indented) + "\n");
    }

    [GeneratedRegex("\"identity\":\\s*\"[^\"]*\"")]
    private static partial Regex IdentityPattern();
}
