using System.Text.Json;
using Json.Schema;

namespace AgentPlatform.Host.Livemap;

public interface ILivemapBuildsSchema
{
    // Null when the builds file satisfies the schema, otherwise a short account of where it does not.
    string? Check(string schemaJson, string buildsJson);
}

// The schema comes from the fork at the commit being changed. It validates only against itself: nothing is fetched for a reference to another document.
public sealed class LivemapBuildsSchemaValidator : ILivemapBuildsSchema
{
    public const int MaxProblemChars = 400;

    private const int MaxDepth = 32;

    private const int MaxReportedErrors = 3;

    private static readonly JsonDocumentOptions _documentOptions = new() { MaxDepth = MaxDepth, AllowTrailingCommas = false };

    public string? Check(string schemaJson, string buildsJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        ArgumentNullException.ThrowIfNull(buildsJson);

        JsonSchema schema;
        JsonDocument file;
        try
        {
            schema = JsonSchema.FromText(schemaJson, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            file = JsonDocument.Parse(buildsJson, _documentOptions);
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or RefResolutionException or InvalidOperationException)
        {
            return Cap($"The schema or the builds file cannot be read: {ex.GetType().Name}.");
        }

        using (file)
        {
            EvaluationResults results;
            try
            {
                results = schema.Evaluate(file.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            }
            catch (Exception ex) when (ex is JsonSchemaException or RefResolutionException or InvalidOperationException)
            {
                return Cap($"The builds file cannot be checked against the schema: {ex.GetType().Name}.");
            }

            return results.IsValid ? null : Cap(Describe(results));
        }
    }

    private static string Describe(EvaluationResults results)
    {
        var errors = (results.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .Take(MaxReportedErrors)
            .Select(detail => $"{detail.InstanceLocation}: {string.Join(' ', detail.Errors!.Values)}")
            .ToList();
        return errors.Count == 0 ? "The builds file does not satisfy the schema." : $"The builds file does not satisfy the schema. {string.Join("; ", errors)}";
    }

    private static string Cap(string text) => text.Length > MaxProblemChars ? text[..MaxProblemChars] : text;
}
