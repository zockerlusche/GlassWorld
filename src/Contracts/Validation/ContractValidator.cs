using System.Collections.Frozen;
using System.Reflection;

namespace GlassWorld.Contracts.Validation;

/// <summary>
/// Validates raw JSON against the contract schemas embedded in this assembly (copied from
/// contracts/schemas at build time). Generated types alone enforce nothing: System.Text.Json ignores
/// unknown fields and fills missing required fields with defaults, so every message must pass here
/// before it is deserialized ("AI output is data", engineering handbook).
/// </summary>
public static class ContractValidator
{
    private const string ResourcePrefix = "GlassWorld.Contracts.Schemas.";

    private static readonly Lazy<FrozenDictionary<string, NJsonSchema.JsonSchema>> Schemas = new(LoadSchemas);

    /// <summary>Names of all schemas (file name without <c>.schema.json</c>), except common.</summary>
    public static IReadOnlyCollection<string> SchemaNames => Schemas.Value.Keys;

    /// <summary>Returns the validation errors of <paramref name="json"/> against a schema (empty = valid).</summary>
    /// <param name="schemaName">Schema name, e.g. <c>decision-response</c>.</param>
    /// <param name="json">Raw JSON text.</param>
    public static IReadOnlyList<string> Validate(string schemaName, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!Schemas.Value.TryGetValue(schemaName, out var schema))
        {
            throw new ArgumentException($"Unknown contract schema '{schemaName}'.", nameof(schemaName));
        }

        try
        {
            return [.. schema.Validate(json).Select(e => $"{e.Kind} at {e.Path}")];
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            return [$"InvalidJson: {ex.Message}"];
        }
    }

    private static FrozenDictionary<string, NJsonSchema.JsonSchema> LoadSchemas()
    {
        var assembly = typeof(ContractValidator).Assembly;
        var sources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(n => n[ResourcePrefix.Length..], n => ReadResource(assembly, n), StringComparer.Ordinal);
        return SchemaBundle.Bundle(sources, forValidation: true)
            .ToFrozenDictionary(
                s => s.Key,
                s => NJsonSchema.JsonSchema.FromJsonAsync(s.Value).GetAwaiter().GetResult(),
                StringComparer.Ordinal);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded schema {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
