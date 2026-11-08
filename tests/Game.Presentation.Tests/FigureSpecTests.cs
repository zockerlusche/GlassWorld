using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Presentation.Tests;

public sealed class FigureSpecTests
{
    private static T Look<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));

    [Fact]
    public void EveryStartInhabitantResolvesToAFigureOfTheRightSize()
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        var host = SimulationHost.CreatePlaceholderWorld(Path.Combine(TestPaths.Root, "content"), 7, new MockDecisionPort(), "de");
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var appearance = Assert.IsType<Simulation.Inhabitants.Appearance>(inhabitant.Appearance);
            var spec = FigureSpec.From(appearance, inhabitant.Sex, figures, look);
            var model = figures.Bodies.First(b => b.Model == spec.Model);
            var stage = look.AgeStages.First(s => s.Id == appearance.AgeStage);
            Assert.Equal(appearance.HeightCm * stage.Scale, spec.Scale * model.ModelHeightCm, 3);
            Assert.Contains(figures.Hair, h => h.Mesh == spec.HairMesh);
        }
    }

    [Fact]
    public void EveryStyleAndStageOfTheAppearanceDataHasAFigureEntry()
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        Assert.All(look.HairStyles, s => Assert.Contains(figures.Hair, h => h.Style == s));
        Assert.All(look.ClothingStyles, s => Assert.Contains(figures.Clothing, c => c.Style == s));
        foreach (var stage in look.AgeStages)
        {
            foreach (var sex in new[] { "female", "male" })
            {
                Assert.True(figures.Bodies.Any(b => b.AgeStage == stage.Id && (b.Sex == sex || b.Sex == "any")), $"no body for {stage.Id}/{sex}");
            }
        }
    }

    [Fact]
    public void BodyModelsAndHairMeshesExistInTheAssets()
    {
        var figures = Look<LookFigures>("figures.json");
        var folder = Path.Combine(TestPaths.Root, "game", "assets", "characters", "glassworld");
        foreach (var model in figures.Bodies.Select(b => b.Model).Distinct())
        {
            var glb = File.ReadAllBytes(Path.Combine(folder, model + ".glb"));
            using var json = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12)));
            var meshes = json.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(n => n.TryGetProperty("name", out var name) ? name.GetString() : null).ToHashSet();
            Assert.All(figures.Hair.Select(h => h.Mesh).Distinct(), mesh => Assert.Contains("Hair_" + mesh, meshes));
        }
    }
}
