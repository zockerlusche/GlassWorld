using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1603: an invention shows its painted icon, else the kit colours of its plan, else nothing.</summary>
public sealed class InventionIconSourceTests
{
    private const string Type = "inv_00a1b2c3";

    private static readonly Lazy<string> Plan = new(() => JsonSerializer.Serialize(ContractSerializer.Deserialize<JobStatus>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "examples", "valid", "job-status.build-plan-bowl.json"))).Result!.BuildPlan!));

    private static BuildKit Kit => ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "build", "kit.json")));

    private static InventionIconSource Icons(string? picture, string? plan, Func<string, bool> exists) =>
        new(t => t == Type, _ => picture, _ => plan, Kit, "pictures", fileExists: exists);

    [Fact]
    public void ThePaintedIconComesFirstWhenItsFileIsThere()
    {
        var icon = Icons("job-1.png", Plan.Value, _ => true).For(Type);
        Assert.Equal(Path.Combine("pictures", "job-1.png"), icon!.PicturePath);
        Assert.Empty(icon.KitColors);

        // Only the file name counts, never a path the result might carry.
        Assert.Equal(Path.Combine("pictures", "evil.png"), Icons("../../evil.png", null, _ => true).For(Type)!.PicturePath);
    }

    [Fact]
    public void WithoutThePictureTheKitColoursOfThePlanShowLargestShareFirst()
    {
        var icon = Icons("job-1.png", Plan.Value, _ => false).For(Type);
        Assert.Null(icon!.PicturePath);
        var kit = Kit.Colors.ToDictionary(c => c.Id, c => c.Color, StringComparer.Ordinal);
        var parts = InventionSystemPlan().Parts;
        var largest = parts.GroupBy(p => p.Color).OrderByDescending(g => g.Sum(p => p.SizeM.X * p.SizeM.Y * p.SizeM.Z)).First().Key;
        Assert.Equal(kit[largest], icon.KitColors[0]);
        Assert.InRange(icon.KitColors.Count, 1, 3);
        Assert.All(icon.KitColors, c => Assert.Contains(c, kit.Values));
    }

    [Fact]
    public void NoInventionOrNoPlanAndNoPictureShowsNothing()
    {
        Assert.Null(Icons(null, Plan.Value, _ => true).For("flint"));
        Assert.Null(Icons(null, null, _ => true).For(Type));
    }

    private static BuildPlan InventionSystemPlan() => Simulation.Inventions.InventionSystem.PlanOf(Plan.Value);
}
