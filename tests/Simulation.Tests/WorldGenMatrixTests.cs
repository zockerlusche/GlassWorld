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
        foreach (var grid in new int?[] { null, 256 })
        {
            foreach (var factor in new[] { 0.6, 1.5 })
            {
                foreach (var pairs in new[] { 1, 4 })
                {
                    foreach (var seed in new ulong[] { 4, 5, 7 })
                    {
                        data.Add(grid, factor, pairs, seed);
                    }
                }
            }
        }

        return data;
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
