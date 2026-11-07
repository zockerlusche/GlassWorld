using System.Text.RegularExpressions;
using GlassWorld.Contracts.Validation;
using NJsonSchema;
using NJsonSchema.CodeGeneration;
using NJsonSchema.CodeGeneration.CSharp;

namespace GlassWorld.Tools.ContractGen;

/// <summary>
/// Generates the C# contract types (one file per message schema, one shared namespace) from the
/// bundled schemas. Types already emitted for an earlier schema (shared definitions from common)
/// are excluded from later files, so every type exists exactly once.
/// </summary>
public static partial class Generator
{
    /// <summary>Namespace of the generated contract types.</summary>
    public const string Namespace = "GlassWorld.Contracts";

    /// <summary>
    /// Generates all files; key = file name, value = content (LF line endings). Message schemas come first,
    /// data file schemas (named in contracts/content-map.json) last, so shared types from common (e.g.
    /// Locale) stay in the message files and do not move when a new data schema is added.
    /// </summary>
    public static async Task<IReadOnlyList<KeyValuePair<string, string>>> GenerateAsync(string schemaDirectory)
    {
        var dataSchemas = DataFileSchemas(schemaDirectory);
        var ordered = SchemaBundle.LoadDirectory(schemaDirectory, forValidation: false)
            .OrderBy(s => dataSchemas.Contains(s.Key))
            .ThenBy(s => s.Key, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<KeyValuePair<string, string>>();
        foreach (var (name, json) in ordered)
        {
            var schema = await JsonSchema.FromJsonAsync(json).ConfigureAwait(false);
            var settings = new CSharpGeneratorSettings
            {
                Namespace = Namespace,
                JsonLibrary = CSharpJsonLibrary.SystemTextJson,
                ClassStyle = CSharpClassStyle.Poco,
                GenerateNullableReferenceTypes = true,
                GenerateOptionalPropertiesAsNullable = true,
                GenerateDataAnnotations = false,
                PropertyNameGenerator = new SnakeToPascalPropertyNames(),
                ExcludedTypeNames = [.. emitted],
            };
            var code = new CSharpGenerator(schema, settings).GenerateFile();
            code = PostProcess(code);
            code = OmitUnsetOptionals(code, OptionalProperties(schema));
            foreach (Match match in TypeDeclaration().Matches(code))
            {
                emitted.Add(match.Groups["name"].Value);
            }

            files.Add(new(ToPascal(name) + ".g.cs", code));
        }

        return files;
    }

    private static HashSet<string> DataFileSchemas(string schemaDirectory)
    {
        var mapPath = Path.Combine(schemaDirectory, "..", "content-map.json");
        if (!File.Exists(mapPath))
        {
            return [];
        }

        var map = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mapPath))!;
        return map["entries"]!.AsArray().Select(e => (string)e!["schema"]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// System.Text.Json ignores <c>[EnumMember]</c>; .NET 9+ reads <c>[JsonStringEnumMemberName]</c>.
    /// Line endings are normalized so the output is identical on Windows and Linux.
    /// </summary>
    private static string PostProcess(string code) =>
        EnumMember().Replace(code, "[System.Text.Json.Serialization.JsonStringEnumMemberName(${value})]")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Optional (not required) JSON properties per generated class: the root schema by its title,
    /// definitions by their key (the bundle keeps the PascalCase definition names as class names).
    /// </summary>
    private static Dictionary<string, HashSet<string>> OptionalProperties(JsonSchema root)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string? className, JsonSchema schema)
        {
            if (string.IsNullOrEmpty(className))
            {
                return;
            }

            var optional = schema.ActualProperties.Where(p => !p.Value.IsRequired).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            if (optional.Count > 0)
            {
                result[className] = optional;
            }
        }

        Add(root.Title, root);
        foreach (var (key, definition) in root.Definitions)
        {
            Add(key, definition.ActualSchema);
        }

        return result;
    }

    /// <summary>
    /// Not-required properties are left out when null instead of being written as <c>null</c>, so a JSON
    /// document round-trips the same through the C# types and pydantic (<c>exclude_unset</c>). Required
    /// properties, nullable or not, are always written.
    /// </summary>
    private static string OmitUnsetOptionals(string code, Dictionary<string, HashSet<string>> optional)
    {
        var lines = code.Split('\n');
        var output = new List<string>(lines.Length);
        string? current = null;
        foreach (var line in lines)
        {
            var declaration = ClassDeclaration().Match(line);
            if (declaration.Success)
            {
                current = declaration.Groups["name"].Value;
            }

            var property = PropertyName().Match(line);
            if (property.Success && current is not null && optional.TryGetValue(current, out var names) && names.Contains(property.Groups["json"].Value))
            {
                var indent = line[..(line.Length - line.TrimStart().Length)];
                output.Add(indent + "[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]");
            }

            output.Add(line);
        }

        return string.Join('\n', output);
    }

    [GeneratedRegex(@"public partial class (?<name>\w+)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\[System\.Text\.Json\.Serialization\.JsonPropertyName\(""(?<json>[^""]+)""\)\]")]
    private static partial Regex PropertyName();

    private static string ToPascal(string kebab) =>
        string.Concat(kebab.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [GeneratedRegex(@"\[System\.Runtime\.Serialization\.EnumMember\(Value = @(?<value>""[^""]*"")\)\]")]
    private static partial Regex EnumMember();

    [GeneratedRegex(@"public (?:partial class|enum) (?<name>\w+)")]
    private static partial Regex TypeDeclaration();

    private sealed class SnakeToPascalPropertyNames : IPropertyNameGenerator
    {
        public string Generate(JsonSchemaProperty property) =>
            string.Concat(property.Name.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
