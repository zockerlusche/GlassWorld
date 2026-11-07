using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Contracts.Validation;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>
/// The C# side judges the shared examples exactly like the Python contract tests, through the same
/// path the core uses at runtime (<see cref="ContractSerializer"/>): every file in
/// contracts/examples/valid passes and round-trips unchanged, every file in invalid is rejected.
/// File names start with the schema name (&lt;schema&gt;.&lt;case&gt;.json).
/// </summary>
public sealed class ContractExamplesTests
{
    /// <summary>Schemas for data files; they are checked through content/ instead of examples.</summary>
    private static readonly HashSet<string> DataFileSchemas = new(StringComparer.Ordinal) { "common", "vocabulary", "hardware-tiers" };

    private static readonly string Root = RepoRoot.Find();
    private static readonly string Examples = Path.Combine(Root, "contracts", "examples");

    public static TheoryData<string> ValidExamples => Files(Path.Combine(Examples, "valid"));

    public static TheoryData<string> InvalidExamples => Files(Path.Combine(Examples, "invalid"));

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void ValidExampleIsAcceptedAndRoundTrips(string file)
    {
        var json = File.ReadAllText(file);
        var type = GeneratedTypeFor(file);

        var model = ContractSerializer.Deserialize(json, type);
        var back = JsonSerializer.Serialize(model, type);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), $"{Path.GetFileName(file)} changed on round trip:\n{back}");
        Assert.Empty(ContractValidator.Validate(SchemaName(file), back));
    }

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void InvalidExampleIsRejectedBeforeDeserializing(string file)
    {
        var json = File.ReadAllText(file);
        var ex = Assert.Throws<ContractViolationException>(() => ContractSerializer.Deserialize(json, GeneratedTypeFor(file)));
        Assert.Equal(SchemaName(file), ex.Schema);
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void EveryMessageSchemaHasValidAndInvalidExamples()
    {
        var valid = Directory.GetFiles(Path.Combine(Examples, "valid")).Select(SchemaName).ToHashSet(StringComparer.Ordinal);
        var invalid = Directory.GetFiles(Path.Combine(Examples, "invalid")).Select(SchemaName).ToHashSet(StringComparer.Ordinal);
        var schemas = Directory.GetFiles(Path.Combine(Root, "contracts", "schemas"), "*.schema.json")
            .Select(f => Path.GetFileName(f)[..^".schema.json".Length])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in valid.Union(invalid))
        {
            Assert.True(schemas.Contains(name), $"examples for unknown schema '{name}'");
        }

        foreach (var schema in schemas.Except(DataFileSchemas))
        {
            Assert.True(valid.Contains(schema), $"message schema '{schema}' has no valid example");
            Assert.True(invalid.Contains(schema), $"message schema '{schema}' has no invalid example");
        }
    }

    [Fact]
    public void SerializerRejectsOutgoingInvalidMessage()
    {
        var valid = File.ReadAllText(Path.Combine(Examples, "valid", "service-error.timeout.json"));
        var error = ContractSerializer.Deserialize<ServiceError>(valid);
        error.ContractVersion = "9.9";

        var ex = Assert.Throws<ContractViolationException>(() => ContractSerializer.Serialize(error));
        Assert.Equal("service-error", ex.Schema);
    }

    [Fact]
    public void BrokenJsonIsAViolationNotACrash()
    {
        var ex = Assert.Throws<ContractViolationException>(() => ContractSerializer.Deserialize<ServiceError>("{ not json"));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void EmbeddedSchemasMatchTheRepository()
    {
        var onDisk = Directory.GetFiles(Path.Combine(Root, "contracts", "schemas"), "*.schema.json")
            .Select(f => Path.GetFileName(f)[..^".schema.json".Length])
            .Where(n => n != "common")
            .Order(StringComparer.Ordinal);
        Assert.Equal(onDisk, ContractValidator.SchemaNames.Order(StringComparer.Ordinal));
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

    private static Type GeneratedTypeFor(string file)
    {
        var typeName = Generator.Namespace + "." + string.Concat(SchemaName(file).Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return typeof(DecisionRequest).Assembly.GetType(typeName, throwOnError: true)!;
    }
}
