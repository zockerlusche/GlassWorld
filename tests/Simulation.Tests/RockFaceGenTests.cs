using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Generator version 4 (TL #20548, W1726): stacks of one-step terraces become one higher rock face with
/// level ground in front; caves stay at the faces and reachable; without the data it is version 3.
/// </summary>
public sealed class RockFaceGenTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    private static GeneratedIsland Generate(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    [Fact]
    public void TheStartIslandAsksForRockFacesInVersionFour()
    {
        var preset = Content.Value.WorldGen.Presets["start_island"];
        Assert.Equal(4, preset.GeneratorVersion);
        Assert.NotNull(preset.Terrain.RockFaces);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void NoTwoOneStepWallsFollowEachOther(ulong seed)
    {
        var preset = Content.Value.WorldGen.Presets["start_island"];
        var island = Generate(seed);
        var figures = WorldGenerator.StepStacks(island.Map, preset.Terrain.CliffMinDrop, preset.Terrain.RockFaces!.LedgeMaxCells, null);
        Assert.True(figures.StackedSteps == 0, $"seed {seed}: {figures.StackedSteps} stacked one-step walls in {figures.StackRuns} runs");
        Assert.True(island.CliffCells > 0, $"seed {seed}: no cliff left");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void CavesStandAtAFaceAndCanBeReachedFromTheGroundInFront(ulong seed)
    {
        var preset = Content.Value.WorldGen.Presets["start_island"];
        var island = Generate(seed);
        var map = island.Map;
        var caves = island.Objects.Where(o => o.TypeId == preset.Caves.ObjectType).ToList();
        Assert.NotEmpty(caves);
        var reach = WorldGenerator.Reachable(map, island.Starts[0].X, island.Starts[0].Z);
        foreach (var cave in caves)
        {
            var cell = map.Cell(cave.X, cave.Z);
            Assert.NotEqual(MapCell.Blocked, cell.WalkCost);
            Assert.True(reach[(cave.Z * map.WidthCells) + cave.X], $"seed {seed}: cave ({cave.X}, {cave.Z}) cannot be reached");
            var face = new[] { (1, 0), (0, -1), (-1, 0), (0, 1) }
                .Any(n => map.Cell(cave.X + n.Item1, cave.Z + n.Item2).Height - cell.Height >= preset.Terrain.CliffMinDrop
                    && map.BiomeAt(cave.X + n.Item1, cave.Z + n.Item2) == preset.Caves.CliffBiome);
            Assert.True(face, $"seed {seed}: cave ({cave.X}, {cave.Z}) has no rock face behind it");
        }
    }

    [Fact]
    public void SameSeedGivesTheSameFacesTwice()
    {
        Assert.Equal(Generate(7).Hash(), Generate(7).Hash());
        Assert.Equal(Generate(2026).Hash(), Generate(2026).Hash());
    }

    /// <summary>Without rock faces in the data the generator is version 3: the old golden hash of seed 1.</summary>
    [Fact]
    public void WithoutRockFacesTheGeneratorIsVersionThree()
    {
        var content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
        var preset = content.WorldGen.Presets["start_island"];
        preset.Terrain.RockFaces = null;
        preset.GeneratorVersion = 3;
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 1);
        Assert.Equal("d64e7fea4c0e163ad497d216220464dd20c067ff54245875cd3067e40e20c3a6", island.Hash());
    }
}
