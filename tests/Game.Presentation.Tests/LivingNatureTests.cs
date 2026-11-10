using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using ObjectPlacement = GlassWorld.Presentation.World.ObjectPlacement;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Living nature (B2-5 R21): the shown wind follows the core's wind calmly in real time, and grass and
/// small plants are drawn in detail levels with the density and distance of the quality level.
/// </summary>
public sealed class LivingNatureTests
{
    private static readonly LookNature Nature = Read<LookNature>("nature.json");

    private static readonly LookQuality Quality = Read<LookQuality>("quality.json");

    [Fact]
    public void TheShownWindFollowsTheCoreWindOverTheSmoothingTime()
    {
        var wind = new WindSway(Nature.Wind);
        wind.Update(0.2, 0.016);
        Assert.Equal(0.2, wind.Strength, 9);

        // A step to full wind: after the smoothing time about 63 % of the way, whatever the frame rate.
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var w = new WindSway(Nature.Wind);
            w.Update(0, 0);
            for (var f = 0; f < (int)Math.Round(Nature.Wind.SmoothRealSeconds * fps); f++)
            {
                w.Update(1, 1.0 / fps);
            }

            Assert.Equal(1 - Math.Exp(-1), w.Strength, 2);
        }

        // No weather (the placeholder world has none): still air.
        var none = new WindSway(Nature.Wind);
        none.Update(null, 0.016);
        none.Update(null, 1);
        Assert.Equal(0, none.Strength);
        Assert.Equal(0, none.OffsetAt(3, 4));
    }

    [Fact]
    public void GustsAtFastForwardStayCalmAndAPauseHoldsThePlantsStill()
    {
        // At x16 the core's wind may flip between frames; the shown wind may only creep, the phase only run on.
        const double Frame = 1.0 / 60;
        var wind = new WindSway(Nature.Wind);
        wind.Update(0, 0);
        var (strength, phase) = (wind.Strength, wind.Phase);
        for (var f = 0; f < 3600; f++)
        {
            wind.Update(f % 2 == 0 ? 1 : 0, Frame);
            Assert.InRange(Math.Abs(wind.Strength - strength), 0, Frame / Nature.Wind.SmoothRealSeconds);
            var step = (wind.Phase - phase + Math.Tau) % Math.Tau;
            Assert.InRange(step, 0, Nature.Wind.SwingsPerSecondFull * Math.Tau * Frame * 1.0001);
            Assert.InRange(wind.Phase, 0, Math.Tau);
            Assert.InRange(Math.Abs(wind.OffsetAt(f, -f)), 0, wind.Strength + 1e-9);
            (strength, phase) = (wind.Strength, wind.Phase);
        }

        // Paused: no real time passes for the look, so strength and phase stay exactly.
        wind.Update(1, 0);
        Assert.Equal((strength, phase), (wind.Strength, wind.Phase));

        // Calm world: a plant swings slowly even in full wind, and the direction is a unit vector.
        Assert.InRange(Nature.Wind.SwingsPerSecondFull, Nature.Wind.SwingsPerSecondCalm, 1.5);
        Assert.Equal(1, (wind.DirectionX * wind.DirectionX) + (wind.DirectionZ * wind.DirectionZ), 9);
    }

    [Fact]
    public void GrassIsThinnedByTheQualityLevelAndTheFarLevelIsAPartOfTheNearOne()
    {
        var (grass, other, layout) = RealGrass();
        Assert.True(grass.Count > 1000, $"the start island has grass ({grass.Count})");
        Assert.All(grass, g => Assert.Contains(g.TypeId, Nature.Grass.Types));
        Assert.All(other, o => Assert.DoesNotContain(o.TypeId, Nature.Grass.Types));
        var all = layout.Values.Sum(l => l.Count);

        var previous = 0;
        foreach (var level in Quality.Levels)
        {
            var chunks = GrassPlan.Chunks(layout, Nature.Grass, level);
            var (near, far) = (chunks.Sum(c => c.Near.Count), chunks.Sum(c => c.Far.Count));

            // The density is the share of plants drawn; the far level keeps far_density_share of them.
            Assert.InRange(near / (double)all, level.GrassDensity - 0.05, level.GrassDensity + 0.05);
            Assert.InRange(far / (double)all, (level.GrassDensity * Nature.Grass.FarDensityShare) - 0.05, (level.GrassDensity * Nature.Grass.FarDensityShare) + 0.05);
            Assert.True(near >= previous, "a higher level never draws fewer plants");
            previous = near;
            Assert.All(chunks, c =>
            {
                Assert.NotEmpty(c.Near);
                Assert.All(c.Far, p => Assert.Contains(p, c.Near));
                Assert.All(c.Near, p => Assert.Equal((c.ChunkX, c.ChunkZ), ((int)Math.Floor(p.X / Nature.Grass.ChunkM), (int)Math.Floor(p.Z / Nature.Grass.ChunkM))));
            });

            // The same plants every time, and a plant stays when the density rises.
            Assert.Equal(chunks.SelectMany(c => c.Near.Select(p => p.ObjectId)), GrassPlan.Chunks(layout, Nature.Grass, level).SelectMany(c => c.Near.Select(p => p.ObjectId)));

            var ranges = GrassPlan.Ranges(Nature.Grass, level);
            Assert.Equal(level.GrassDistanceM, ranges.FarEndM);
            Assert.Equal(level.GrassDistanceM * Nature.Grass.NearShare, ranges.NearEndM);
            Assert.True(ranges.HasFar);
        }

        Assert.Equal(Quality.Levels.Single(l => l.Id == Quality.DefaultLevel), GrassPlan.Level(Quality));
    }

    [Fact]
    public void NoGrassDistanceOrDensityDrawsNoGrass()
    {
        var (_, _, layout) = RealGrass();
        var level = Quality.Levels.First();
        Assert.Empty(GrassPlan.Chunks(layout, Nature.Grass, new QualityLevel { Id = "off", GrassDensity = 0, GrassDistanceM = 50, Shadows = level.Shadows, Outline = level.Outline }));
        Assert.Empty(GrassPlan.Chunks(layout, Nature.Grass, new QualityLevel { Id = "off", GrassDensity = 1, GrassDistanceM = 0, Shadows = level.Shadows, Outline = level.Outline }));
        Assert.InRange(GrassPlan.Rank("decor-1-10-20-3"), 0, 1);
        Assert.Equal(GrassPlan.Rank("decor-1-10-20-3"), GrassPlan.Rank("decor-1-10-20-3"));
    }

    [Fact]
    public void TheNatureDataNamesKnownColoursAndDecorTypes()
    {
        var palette = Read<LookPalette>("palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Nature.Snow.Color, palette);
        Assert.Contains(Nature.LeafFall.Color, palette);
        var objects = Read<LookObjects>("objects.json");
        var decor = objects.Decor!.Select(d => d.Type).ToHashSet(StringComparer.Ordinal);
        Assert.All(Nature.Grass.Types, t => Assert.Contains(t, decor));

        // Plants sway, stones and finds do not; a tree's top moves more than a tuft's.
        var looks = new ObjectLooks(objects, palette);
        Assert.All(looks.Visuals.Where(v => v.Shape is ObjectLookShape.Rock or ObjectLookShape.Stone or ObjectLookShape.Log or ObjectLookShape.Pool), v => Assert.Equal(0, v.SwayM));
        Assert.All(looks.Visuals.Where(v => v.Shape is ObjectLookShape.Tuft or ObjectLookShape.Broadleaf_tree or ObjectLookShape.Reeds), v => Assert.True(v.SwayM > 0));
        Assert.True(looks.Visuals.First(v => v.Shape == ObjectLookShape.Broadleaf_tree).SwayM > looks.Visuals.First(v => v.Shape == ObjectLookShape.Tuft).SwayM);

        // The leaves fall for about their lifetime through the height of their box.
        var leaves = Nature.LeafFall;
        Assert.InRange(leaves.FallMPerS * leaves.LifetimeS, leaves.HeightM * 0.8, leaves.HeightM * 1.5);
    }

    private static (IReadOnlyList<WorldObject> Grass, IReadOnlyList<WorldObject> Other, IReadOnlyDictionary<string, List<ObjectPlacement>> Layout) RealGrass()
    {
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7);
        var objects = Read<LookObjects>("objects.json");
        var cellM = content.WorldGen.Presets["start_island"].Grid.CellSizeCm / 100.0;
        var (grass, other) = GrassPlan.Split(Decor.Plan(objects.Decor, island, cellM), Nature.Grass);
        var looks = new ObjectLooks(objects, Read<LookPalette>("palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
        return (grass, other, looks.Layout(grass, (_, _) => 0));
    }

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
}
