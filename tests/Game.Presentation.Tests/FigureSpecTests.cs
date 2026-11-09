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
            Assert.NotEmpty(spec.ClothingMeshes);
        }
    }

    [Theory]
    [InlineData("shirt_trousers", RegionPaint.Bottom, RegionPaint.Bottom, RegionPaint.Bottom)]
    [InlineData("shirt_shorts", RegionPaint.Bottom, RegionPaint.Skin, RegionPaint.Skin)]
    [InlineData("dress", RegionPaint.Top, RegionPaint.Top, RegionPaint.Skin)]
    [InlineData("shirt_skirt", RegionPaint.Bottom, RegionPaint.Skin, RegionPaint.Skin)]
    [InlineData("tunic", RegionPaint.Top, RegionPaint.Skin, RegionPaint.Skin)]
    public void BodyRegionsUnderClothingTakeItsColourTheRestIsSkin(string outfit, RegionPaint thigh, RegionPaint lowerThigh, RegionPaint shin)
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        var spec = FigureSpec.From(Record(outfit, "short"), "female", figures, look);
        Assert.Equal((thigh, lowerThigh, shin, RegionPaint.Skin), (spec.Thigh, spec.LowerThigh, spec.Shin, spec.Forearm));
        var parts = look.Outfits.First(o => o.Id == outfit).Parts;
        Assert.Equal(parts.Select(p => figures.ClothingParts.First(c => c.Id == p).Mesh), spec.ClothingMeshes);
    }

    [Fact]
    public void BaldMeansNoHairMeshAndUnknownEntriesAreDataErrors()
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        Assert.Null(FigureSpec.From(Record("dress", "bald"), "male", figures, look).HairMesh);
        Assert.Throws<InvalidDataException>(() => FigureSpec.From(Record("no_such_outfit", "short"), "male", figures, look));
        Assert.Throws<InvalidDataException>(() => FigureSpec.From(Record("dress", "mohawk"), "male", figures, look));
    }

    [Fact]
    public void EveryStyleAndStageOfTheAppearanceDataHasAFigureEntry()
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        Assert.All(look.HairStyles, s => Assert.Contains(figures.Hair, h => h.Style == s.Id));
        Assert.All(look.Outfits.SelectMany(o => o.Parts), p => Assert.Contains(figures.ClothingParts, c => c.Id == p));
        Assert.All(figures.ClothingParts, c => Assert.Contains(look.Outfits, o => o.Parts.Contains(c.Id)));
        foreach (var stage in look.AgeStages)
        {
            foreach (var sex in new[] { "female", "male" })
            {
                Assert.True(figures.Bodies.Any(b => b.AgeStage == stage.Id && (b.Sex == sex || b.Sex == "any")), $"no body for {stage.Id}/{sex}");
            }
        }
    }

    [Fact]
    public void BodyModelsHairAndClothingMeshesAndBlendShapesExistInTheAssets()
    {
        var figures = Look<LookFigures>("figures.json");
        var look = Look<LookAppearance>("appearance.json");
        var folder = Path.Combine(TestPaths.Root, "game", "assets", "characters", "glassworld");
        foreach (var model in figures.Bodies.Select(b => b.Model).Distinct())
        {
            var glb = File.ReadAllBytes(Path.Combine(folder, model + ".glb"));
            Assert.SkipWhen(GlassWorld.Presentation.Audio.BaseSoundCheck.IsLfsPointer(glb), "LFS files not checked out; the game smoke run loads the figures (ISLAND_OK figures=N).");
            using var json = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12)));
            var meshes = json.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(n => n.TryGetProperty("name", out var name) ? name.GetString() : null).ToHashSet();
            Assert.All(figures.Hair.Select(h => h.Mesh).OfType<string>().Distinct(), mesh => Assert.Contains("Hair_" + mesh, meshes));
            Assert.All(figures.ClothingParts.Select(c => c.Mesh).Distinct(), mesh => Assert.Contains("Cloth_" + mesh, meshes));

            // Every blend shape the data can ask for exists on the body and on every clothing part, so
            // the clothing follows the body (F4); the sex proportions exist on the bodies of their sex.
            var targets = json.RootElement.GetProperty("meshes").EnumerateArray().ToDictionary(
                m => m.GetProperty("name").GetString()!,
                m => m.TryGetProperty("extras", out var extras) && extras.TryGetProperty("targetNames", out var names)
                    ? names.EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal)
                    : [],
                StringComparer.Ordinal);
            var sexes = figures.Bodies.Where(b => b.Model == model).Select(b => b.Sex).ToHashSet(StringComparer.Ordinal);
            var stages = figures.Bodies.Where(b => b.Model == model).Select(b => b.AgeStage).ToHashSet(StringComparer.Ordinal);
            var wanted = look.Shapes.Select(s => s.Id)
                .Concat(look.SexShapes.Where(s => sexes.Contains(s.Sex.ToString().ToLowerInvariant()) && s.AgeStages.Any(stages.Contains)).Select(s => s.Id))
                .Select(id => "body_" + id).ToList();
            foreach (var mesh in figures.ClothingParts.Select(c => "Cloth_" + c.Mesh).Prepend("Body"))
            {
                var names = targets.First(t => t.Key == mesh || t.Key.StartsWith(mesh + ".", StringComparison.Ordinal)).Value;
                Assert.All(wanted, shape => Assert.Contains(shape, names));
            }
        }
    }

    private static Simulation.Inhabitants.Appearance Record(string outfit, string hair) =>
        new(172, 500, "#e3b089", hair, "#682707", outfit, "#39656b", "#814611", "adult", new Dictionary<string, double>(), 1.0);
}
