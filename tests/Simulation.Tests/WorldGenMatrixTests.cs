using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1833 (B2808, the generator always builds): every combination of the new-world controls gives an island that passes,
/// with its start places. The seeds are ones that failed before the start supply could place springs (medium, 4 pairs).
/// </summary>
public sealed class WorldGenMatrixTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    public static TheoryData<int?, double, int, ulong> Combinations()
    {
        var data = new TheoryData<int?, double, int, ulong>();
        // The seeds that failed before springs could be placed (medium, 4 pairs), and the small island once each.
        foreach (var factor in new[] { 0.6, 1.5 })
        {
            foreach (var seed in new ulong[] { 4, 5, 7 })
            {
                data.Add(256, factor, 4, seed);
            }

            data.Add(null, factor, 4, 1);
            data.Add(null, factor, 1, 1);
        }

        return data;
    }

    /// <summary>The B2808 matrix: every terrain x distribution x resources option of the data, on both sizes.</summary>
    public static TheoryData<int?, double, double, double, ulong> TerrainCombinations()
    {
        var options = Contracts.Validation.ContractSerializer.Deserialize<Contracts.WorldOptions>(
            File.ReadAllText(Path.Combine(TestData.Root, "content", "player", "world_options.json")));
        var data = new TheoryData<int?, double, double, double, ulong>();
        foreach (var grid in new int?[] { null, 256 })
        {
            foreach (var terrain in options.Terrains!)
            {
                foreach (var distribution in options.Distributions!.Where(d => d.Id != "normal"))
                {
                    foreach (var resources in options.Resources!.Where(r => r.Id != "normal"))
                    {
                        data.Add(grid, terrain.HeightFactor, distribution.ClusterFactor, resources.Factor, grid is null ? 1UL : 2UL);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TerrainCombinations))]
    public void EveryTerrainDistributionAndResourceChoiceBuilds(int? grid, double terrain, double cluster, double resources, ulong seed)
    {
        // A1833 (B2808): the generator always builds - flat, hilly or mountainous, scattered or clustered, sparse or rich.
        var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed, grid, resources, 2, terrain, cluster);
        Assert.True(island.Passed, string.Join(", ", island.Check.Where(c => !c.Passed && !c.ReportOnly).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));
        Assert.Equal(4, island.Starts.Count);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryCombinationBuildsAnIslandThatPasses(int? grid, double factor, int pairs, ulong seed)
    {
        var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed, grid, factor, pairs);
        Assert.True(island.Passed, string.Join(", ", island.Check.Where(c => !c.Passed && !c.ReportOnly).Select(c => $"{c.Id} {c.Reachable}/{c.Min}")));
        Assert.Equal(pairs * 2, island.Starts.Count);
    }
}
