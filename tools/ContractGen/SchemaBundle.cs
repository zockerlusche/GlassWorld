using System.Text.Json.Nodes;

namespace GlassWorld.Tools.ContractGen;

/// <summary>
/// Turns the JSON Schema 2020-12 sources in <c>contracts/schemas</c> into self-contained schemas that
/// NJsonSchema understands. The sources stay the single source of truth; this is only an adapter for
/// code generation and C# validation:
/// <list type="bullet">
/// <item>copies <c>common.schema.json#/$defs</c> into every schema and rewrites cross-file refs,</item>
/// <item>renames <c>$defs</c> to <c>definitions</c> (NJsonSchema does not resolve nested <c>$defs</c>),</item>
/// <item>rewrites <c>anyOf: [X, null]</c> to <c>oneOf: [null, X]</c> (nullable types),</item>
/// <item>for validation only: rewrites <c>const: v</c> to <c>enum: [v]</c> (the NJsonSchema validator ignores
/// <c>const</c>; for code generation <c>const</c> stays, so the property remains a plain string).</item>
/// </list>
/// </summary>
public static class SchemaBundle
{
    /// <summary>Name of the shared definitions file.</summary>
    public const string CommonFileName = "common.schema.json";

    /// <summary>
    /// Returns the bundled schema JSON for every message schema (all files except common), keyed by
    /// the file name without <c>.schema.json</c>, in ordinal order.
    /// </summary>
    /// <param name="schemaDirectory">Folder with the 2020-12 source schemas.</param>
    /// <param name="forValidation">True to also rewrite <c>const</c> for the NJsonSchema validator.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Load(string schemaDirectory, bool forValidation)
    {
        var commonPath = Path.Combine(schemaDirectory, CommonFileName);
        var common = ParseObject(commonPath)["$defs"] as JsonObject
            ?? throw new InvalidDataException($"{commonPath} has no $defs.");

        var result = new List<KeyValuePair<string, string>>();
        foreach (var file in Directory.GetFiles(schemaDirectory, "*.schema.json").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) == CommonFileName)
            {
                continue;
            }

            var schema = ParseObject(file);
            var definitions = new JsonObject();
            foreach (var (key, value) in common)
            {
                definitions[key] = value?.DeepClone();
            }

            if (schema["$defs"] is JsonObject own)
            {
                foreach (var (key, value) in own)
                {
                    if (definitions.ContainsKey(key))
                    {
                        throw new InvalidDataException($"{file}: $defs/{key} shadows a definition in {CommonFileName}.");
                    }

                    definitions[key] = value?.DeepClone();
                }
            }

            schema.Remove("$defs");
            schema["$schema"] = "http://json-schema.org/draft-07/schema#";
            schema["definitions"] = definitions;
            var name = Path.GetFileName(file)[..^".schema.json".Length];
            result.Add(new(name, Rewrite(schema, forValidation)!.ToJsonString()));
        }

        return result;
    }

    private static JsonObject ParseObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException($"{path} is not a JSON object.");

    private static JsonNode? Rewrite(JsonNode? node, bool forValidation)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray([.. array.Select(n => Rewrite(n, forValidation))]);
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = Rewrite(value, forValidation);
                }

                if (result["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target))
                {
                    result["$ref"] = target
                        .Replace(CommonFileName + "#/$defs/", "#/definitions/", StringComparison.Ordinal)
                        .Replace("#/$defs/", "#/definitions/", StringComparison.Ordinal);
                }

                if (result["anyOf"] is JsonArray anyOf && anyOf.Any(IsNullType))
                {
                    var oneOf = new JsonArray(new JsonObject { ["type"] = "null" });
                    foreach (var option in anyOf.Where(o => !IsNullType(o)))
                    {
                        oneOf.Add(option?.DeepClone());
                    }

                    result.Remove("anyOf");
                    result["oneOf"] = oneOf;
                }

                if (forValidation && result.ContainsKey("const"))
                {
                    var constant = result["const"]?.DeepClone();
                    result.Remove("const");
                    result["enum"] = new JsonArray(constant);
                }

                return result;
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsNullType(JsonNode? node) =>
        node is JsonObject { Count: 1 } obj
        && obj["type"] is JsonValue type
        && type.TryGetValue<string>(out var value)
        && value == "null";
}
