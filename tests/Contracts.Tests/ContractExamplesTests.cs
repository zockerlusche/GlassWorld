using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>
/// The C# side judges the shared examples exactly like the Python contract tests:
/// every file in contracts/examples/valid is valid, every file in invalid is invalid,
/// every vocabulary file is valid. File names start with the schema name (&lt;schema&gt;.&lt;case&gt;.json).
/// </summary>
public sealed class ContractExamplesTests
{
    private static readonly string Root = RepoRoot.Find();
    private static readonly string Examples = Path.Combine(Root, "contracts", "examples");

    private static readonly Lazy<IReadOnlyDictionary<string, NJsonSchema.JsonSchema>> Schemas = new(() =>
        SchemaBundle.Load(Path.Combine(Root, "contracts", "schemas"), forValidation: true)
            .ToDictionary(s => s.Key, s => NJsonSchema.JsonSchema.FromJsonAsync(s.Value).GetAwaiter().GetResult(), StringComparer.Ordinal));

    public static TheoryData<string> ValidExamples => Files(Path.Combine(Examples, "valid"));

    public static TheoryData<string> InvalidExamples => Files(Path.Combine(Examples, "invalid"));

    public static TheoryData<string> VocabularyFiles => Files(Path.Combine(Root, "content", "vocabulary"));

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void ValidExampleIsValid(string file)
    {
        var errors = SchemaFor(file).Validate(File.ReadAllText(file));
        Assert.True(errors.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", errors.Select(e => $"{e.Kind} at {e.Path}"))}");
    }

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void InvalidExampleIsRejected(string file)
    {
        var errors = SchemaFor(file).Validate(File.ReadAllText(file));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [MemberData(nameof(VocabularyFiles))]
    public void VocabularyFileIsValid(string file)
    {
        var errors = Schemas.Value["vocabulary"].Validate(File.ReadAllText(file));
        Assert.True(errors.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", errors.Select(e => $"{e.Kind} at {e.Path}"))}");
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void ValidExampleRoundTripsThroughGeneratedTypes(string file)
    {
        var type = GeneratedTypeFor(file);
        var json = File.ReadAllText(file);
        var model = JsonSerializer.Deserialize(json, type);
        Assert.NotNull(model);
        var back = JsonSerializer.Serialize(model, type);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), $"{Path.GetFileName(file)} changed on round trip:\n{back}");
    }

    [Fact]
    public void EveryMessageSchemaHasValidAndInvalidExamples()
    {
        foreach (var schema in Schemas.Value.Keys.Where(k => k != "vocabulary"))
        {
            Assert.Contains(Directory.GetFiles(Path.Combine(Examples, "valid")), f => SchemaName(f) == schema);
            Assert.Contains(Directory.GetFiles(Path.Combine(Examples, "invalid")), f => SchemaName(f) == schema);
        }
    }

    [Fact]
    public async Task GeneratedTypesAreUpToDate()
    {
        var files = await Generator.GenerateAsync(Path.Combine(Root, "contracts", "schemas"));
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(Root, "src", "Contracts", "Generated", name);
            Assert.True(File.Exists(path), $"missing {name}; run: dotnet run --project tools/ContractGen");
            Assert.True(
                File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) == content,
                $"{name} is outdated; run: dotnet run --project tools/ContractGen");
        }
    }

    private static TheoryData<string> Files(string directory) =>
        [.. Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal)];

    private static string SchemaName(string file) => Path.GetFileName(file).Split('.')[0];

    private static NJsonSchema.JsonSchema SchemaFor(string file) =>
        Schemas.Value.TryGetValue(SchemaName(file), out var schema)
            ? schema
            : throw new InvalidOperationException($"No schema for example {Path.GetFileName(file)}.");

    private static Type GeneratedTypeFor(string file)
    {
        var typeName = Generator.Namespace + "." + string.Concat(SchemaName(file).Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return typeof(DecisionRequest).Assembly.GetType(typeName, throwOnError: true)!;
    }
}
