using System.Text.Json.Nodes;

namespace GlassWorld.Contracts.Validation;

/// <summary>
/// Turns the JSON Schema 2020-12 sources in <c>contracts/schemas</c> into self-contained schemas that
/// NJsonSchema understands. The sources stay the single source of truth; this is only an adapter for
/// code generation (tools/ContractGen links this file) and for runtime validation:
/// <list type="bullet">
/// <item>copies <c>common.schema.json#/$defs</c> into every schema and rewrites cross-file refs,</item>
/// <item>renames <c>$defs</c> to <c>definitions</c> (NJsonSchema does not resolve nested <c>$defs</c>),</item>
/// <item>rewrites <c>anyOf: [X, null]</c> to <c>oneOf: [null, X]</c> (nullable types),</item>
/// <item>for validation only: rewrites <c>const: v</c> to <c>enum: [v]</c> (the NJsonSchema validator ignores
/// <c>const</c>; for code generation <c>const</c> stays, so the property remains a plain string),</item>
/// <item>rejects keywords next to <c>$ref</c> other than annotations: draft-07 tooling ignores them, so a
/// constraint written there would silently not apply.</item>
/// </list>
/// </summary>
internal static class SchemaBundle
{
    /// <summary>File name of the shared definitions.</summary>
    public const string CommonFileName = "common.schema.json";

    /// <summary>File name suffix of every schema.</summary>
    public const string Suffix = ".schema.json";

    private static readonly HashSet<string> AllowedRefSiblings = new(StringComparer.Ordinal) { "$ref", "description", "title", "$comment" };

    /// <summary>Reads all <c>*.schema.json</c> files of a folder and bundles them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LoadDirectory(string schemaDirectory, bool forValidation) =>
        Bundle(
            Directory.GetFiles(schemaDirectory, "*" + Suffix)
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText, StringComparer.Ordinal),
            forValidation);

    /// <summary>
    /// Bundles the given source schemas (file name → JSON text). Returns one entry per schema except
    /// common, keyed by the file name without <c>.schema.json</c>, in ordinal order.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Bundle(IReadOnlyDictionary<string, string> sources, bool forValidation)
    {
        if (!sources.TryGetValue(CommonFileName, out var commonText))
        {
            throw new InvalidDataException($"{CommonFileName} is missing.");
        }

        var common = (ParseObject(CommonFileName, commonText)["$defs"] as JsonObject)
            ?? throw new InvalidDataException($"{CommonFileName} has no $defs.");
        CheckRefSiblings(CommonFileName, common);

        var result = new List<KeyValuePair<string, string>>();
        foreach (var (fileName, text) in sources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (fileName == CommonFileName)
            {
                continue;
            }

            var schema = ParseObject(fileName, text);
            CheckRefSiblings(fileName, schema);
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
                        throw new InvalidDataException($"{fileName}: $defs/{key} shadows a definition in {CommonFileName}.");
                    }

                    definitions[key] = value?.DeepClone();
                }
            }

            schema.Remove("$defs");
            schema["$schema"] = "http://json-schema.org/draft-07/schema#";
            schema["definitions"] = definitions;
            result.Add(new(fileName[..^Suffix.Length], Rewrite(schema, forValidation)!.ToJsonString()));
        }

        return result;
    }

    private static JsonObject ParseObject(string name, string text) =>
        JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{name} is not a JSON object.");

    private static void CheckRefSiblings(string fileName, JsonNode? node, string path = "#")
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    CheckRefSiblings(fileName, array[i], $"{path}/{i}");
                }

                break;
            case JsonObject obj:
                if (obj.ContainsKey("$ref"))
                {
                    var extra = obj.Select(p => p.Key).Where(k => !AllowedRefSiblings.Contains(k)).ToList();
                    if (extra.Count > 0)
                    {
                        throw new InvalidDataException(
                            $"{fileName}: {path} has keywords next to $ref ({string.Join(", ", extra)}); they would be ignored. Wrap them in allOf.");
                    }
                }

                foreach (var (key, value) in obj)
                {
                    CheckRefSiblings(fileName, value, $"{path}/{key}");
                }

                break;
            default:
                break;
        }
    }

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
