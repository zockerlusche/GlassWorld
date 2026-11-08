using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>The screenshot plan (B2 R32): points × projections × phases, optionally × epochs, seasons and weather.</summary>
public sealed class ShotPlanTests
{
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    [Fact]
    public void WithoutTheNewListsThePlanAndTheFileNamesStayAsBefore()
    {
        var shots = Read<LookShots>("shots.json");
        shots.Epochs = shots.Seasons = shots.Weather = null;
        var plan = ShotPlan.Build(shots, Seasons(), None);

        // Exactly the names the tool wrote before the lists existed, in the same order.
        var before = shots.Points.SelectMany(p => shots.Projections.SelectMany(r => shots.Phases.Select(h => $"island_{p.Id}_{r}_{h}_1920x1080.png")));
        Assert.Equal(before, plan.Select(s => s.FileName(1920, 1080)));
        Assert.All(plan, s => Assert.Equal((null, null, null), (s.Epoch, s.Season, s.Weather)));
    }

    [Fact]
    public void ListsMultiplyThePlanWithDeterministicNamesAndNoteWhatHasNoLook()
    {
        var shots = Shots();
        shots.Epochs = ["wilderness", "metal"];
        shots.Seasons = ["winter"];
        shots.Weather = ["clear", "rain"];
        var plan = ShotPlan.Build(shots, Seasons(), new HashSet<string>(["clear"], StringComparer.Ordinal));

        Assert.Equal(2 * 1 * 2 * 2 * 2 * 2, plan.Count);
        var names = plan.Select(s => s.FileName(800, 600)).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("island_close_orthographic_dusk_metal_winter_rain-nolook_800x600.png", names);
        Assert.Contains("island_start_perspective_day_wilderness_winter_clear_800x600.png", names);

        // One epoch and season after the other, so the island is recoloured only when they change.
        var changes = plan.Zip(plan.Skip(1)).Count(p => (p.First.Epoch, p.First.Season) != (p.Second.Epoch, p.Second.Season));
        Assert.Equal(1, changes);
        Assert.Equal(names, ShotPlan.Build(shots, Seasons(), new HashSet<string>(["clear"], StringComparer.Ordinal)).Select(s => s.FileName(800, 600)));

        shots.Epochs = shots.Weather = null;
        shots.Seasons = ["monsoon"];
        Assert.All(ShotPlan.Build(shots, Seasons(), None), s => Assert.EndsWith("_monsoon-nolook_800x600.png", s.FileName(800, 600), StringComparison.Ordinal));
    }

    [Fact]
    public void ThePaletteOfAPictureIsBaseThenSeasonThenEpoch()
    {
        var palette = Read<LookPalette>("palette.json");
        var epochs = Read<LookEpochs>("epochs.json");
        string Color(LookPalette p, string id) => p.Colors.Single(c => c.Id == id).Color;
        var rock = Color(palette, "rock");
        var foliage = Color(palette, "foliage");

        var plain = ShotPlan.PaletteFor(palette, epochs, null, null);
        Assert.Equal(palette.Colors.Select(c => (c.Id, c.Color)), plain.Colors.Select(c => (c.Id, c.Color)));

        var metal = epochs.Epochs.Single(e => e.Id == "metal").Colors.Single(c => c.Id == "rock").Color;
        var autumn = palette.Seasons.Single(s => s.Season == "autumn").Colors.Single(c => c.Id == "foliage").Color;
        var both = ShotPlan.PaletteFor(palette, epochs, "metal", "autumn");
        Assert.Equal((metal, autumn), (Color(both, "rock"), Color(both, "foliage")));
        Assert.Equal(palette.Colors.Count, both.Colors.Count);

        // The base palette stays as it was; an unknown epoch is a data error.
        Assert.Equal((rock, foliage), (Color(palette, "rock"), Color(palette, "foliage")));
        Assert.Throws<InvalidDataException>(() => ShotPlan.PaletteFor(palette, epochs, "space_age", null));
    }

    [Fact]
    public void AnEpochPaletteReachesTheTerrainColoursItNames()
    {
        // The island is recoloured by rebuilding its meshes: the epoch's colours end up in the vertex colours.
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7);
        var preset = content.WorldGen.Presets["start_island"];
        var terrain = Read<LookTerrain>("terrain.json");
        var palette = Read<LookPalette>("palette.json");
        var epochs = Read<LookEpochs>("epochs.json");
        TerrainLook LookOf(string? epoch) => TerrainLook.From(terrain, ShotPlan.PaletteFor(palette, epochs, epoch, null), island.Map, preset);
        List<float> Colors(TerrainLook look) => [.. Enumerable.Range(0, island.Map.ChunksZ)
            .SelectMany(z => Enumerable.Range(0, island.Map.ChunksX).Select(x => (x, z)))
            .SelectMany(c => TerrainMesher.Ground(island.Map, c.x, c.z, look).Colors)];

        // Palette ids the terrain draws with; an epoch changes the terrain only through these.
        var used = terrain.BiomeColors.Select(b => b.Color)
            .Concat([terrain.CliffColor, terrain.BedColor, terrain.Water.SeaShallow, terrain.Water.SeaDeep, terrain.Water.Fresh, terrain.Water.Swamp])
            .ToHashSet(StringComparer.Ordinal);
        var plain = LookOf(null);
        Assert.Equal(Colors(plain), Colors(LookOf("wilderness")));
        foreach (var epoch in epochs.Epochs.Where(e => e.Colors.Any(c => used.Contains(c.Id))))
        {
            var look = LookOf(epoch.Id);
            Assert.NotEqual(plain.BiomeColors, look.BiomeColors);
            Assert.NotEqual(Colors(plain), Colors(look));
        }

        // With today's data only "rock" (biome rocks_hills) of the epoch colours is a terrain colour.
        Assert.Equal(["rock"], epochs.Epochs.SelectMany(e => e.Colors).Select(c => c.Id).Where(used.Contains).Distinct());
    }

    private static LookShots Shots() => new()
    {
        ContractVersion = "0.2",
        Points =
        [
            new ShotPoint { Id = "start", TargetXPermille = 500, TargetZPermille = 500, YawDeg = 30, DistanceM = 60 },
            new ShotPoint { Id = "close", TargetXPermille = 500, TargetZPermille = 500, YawDeg = 120, DistanceM = 30 },
        ],
        Projections = ["perspective", "orthographic"],
        Phases = ["day", "dusk"],
        SettleFrames = 12,
    };

    private static HashSet<string> Seasons() =>
        Read<LookPalette>("palette.json").Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal);

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
}
