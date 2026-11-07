using System.Text.Json.Nodes;
using GlassWorld.Contracts.Validation;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>
/// The generated root type is named after the schema title, and <see cref="ContractSerializer"/>
/// finds the schema from that type name. Both only meet if the file name is the title in kebab case.
/// </summary>
public sealed class SchemaNamingTests
{
    private static readonly string Schemas = Path.Combine(RepoRoot.Find(), "contracts", "schemas");

    public static TheoryData<string> SchemaFiles =>
        [.. Directory.GetFiles(Schemas, "*.schema.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(SchemaFiles))]
    public void FileNameIsTheTitleInKebabCase(string file)
    {
        var title = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(Schemas, file)))!["title"]!;
        var expected = file[..^".schema.json".Length];
        Assert.Equal(expected, KebabCase(title));
    }

    [Theory]
    [InlineData("HealthStatus", "health-status")]
    [InlineData("I18n", "i18n")]
    [InlineData("Vocabulary", "vocabulary")]
    public void KebabCaseMatchesTheSerializer(string title, string expected) => Assert.Equal(expected, KebabCase(title));

    /// <summary>Same rule as <see cref="ContractSerializer.SchemaNameOf"/>, applied to a title string.</summary>
    private static string KebabCase(string title) =>
        string.Concat(title.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
