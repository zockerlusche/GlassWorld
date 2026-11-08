using GlassWorld.Contracts.Validation;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>Cross-file checks for the construction kit (D471 D6) that the schema cannot express.</summary>
public sealed class BuildKitTests
{
    private static readonly string Root = RepoRoot.Find();

    private static BuildKit Kit() =>
        ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(Root, "content", "build", "kit.json")));

    [Fact]
    public void KitShapesAreShapeVocabulary()
    {
        var vocabulary = ContractSerializer.Deserialize<Vocabulary>(File.ReadAllText(Path.Combine(Root, "content", "vocabulary", "shapes.json")));
        Assert.Equal(VocabularyKind.Shape, vocabulary.Kind);
        var known = vocabulary.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Kit().Shapes, s => Assert.Contains(s, known));
    }

    [Fact]
    public void KitColourIdsAreUnique()
    {
        var ids = Kit().Colors.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExampleBuildingPlansUseOnlyKitShapesAndColours()
    {
        var kit = Kit();
        var colours = kit.Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var files = Directory.GetFiles(Path.Combine(Root, "contracts", "examples", "valid"), "job-status.build-plan-*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var plan = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(file)).Result!.BuildPlan!;
            Assert.All(plan.Parts, p => Assert.Contains(p.Shape, kit.Shapes));
            Assert.All(plan.Parts, p => Assert.Contains(p.Color, colours));
            Assert.True(plan.Parts.Count <= kit.MaxParts, $"{Path.GetFileName(file)} has more parts than the kit allows");
        }
    }
}
