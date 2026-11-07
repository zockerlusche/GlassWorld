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

    /// <summary>Generates all files; key = file name, value = content (LF line endings).</summary>
    public static async Task<IReadOnlyList<KeyValuePair<string, string>>> GenerateAsync(string schemaDirectory)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<KeyValuePair<string, string>>();
        foreach (var (name, json) in SchemaBundle.LoadDirectory(schemaDirectory, forValidation: false))
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
            foreach (Match match in TypeDeclaration().Matches(code))
            {
                emitted.Add(match.Groups["name"].Value);
            }

            files.Add(new(ToPascal(name) + ".g.cs", code));
        }

        return files;
    }

    /// <summary>
    /// System.Text.Json ignores <c>[EnumMember]</c>; .NET 9+ reads <c>[JsonStringEnumMemberName]</c>.
    /// Line endings are normalized so the output is identical on Windows and Linux.
    /// </summary>
    private static string PostProcess(string code) =>
        EnumMember().Replace(code, "[System.Text.Json.Serialization.JsonStringEnumMemberName(${value})]")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

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
