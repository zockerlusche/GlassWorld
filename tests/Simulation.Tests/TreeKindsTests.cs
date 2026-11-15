using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1105 H1 (A1722, B2738/B2741): tree kinds and the few physical sizes are data - every shade-casting tree says
/// how well one holds on to it and whether birds may nest in it; the new kinds grow on the start island.
/// </summary>
public sealed class TreeKindsTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void EveryTreeSaysItsGripAndWhetherBirdsMayNest()
    {
        var trees = Content.Objects.TypeIds.Where(t => Content.Objects.Params(t).ShadeHeightCm is not null).ToList();
        Assert.Contains("birch_tree", trees);
        Assert.Contains("young_tree", trees);
        foreach (var tree in trees)
        {
            var p = Content.Objects.Params(tree);
            Assert.True(p.GripPermille is not null, $"{tree} has no grip");
            Assert.True(p.NestPossible is not null, $"{tree} does not say whether birds may nest");
        }

        // A smooth trunk is hard to hold, a branchy one easy (data, D1105 4a).
        Assert.True(Content.Objects.Params("birch_tree").GripPermille < Content.Objects.Params("deciduous_tree").GripPermille);
    }

    [Fact]
    public void TheNewKindsGrowOnTheStartIsland()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        Assert.Contains(host.Objects.All, o => o.TypeId == "birch_tree");
        Assert.Contains(host.Objects.All, o => o.TypeId == "young_tree");
    }

    [Fact]
    public void LooseWoodFloatsAndStoneSinksByTheData()
    {
        // D1105 W1 needs only the size; whether a thing floats is the core's later rule (below 1000 permille).
        Assert.True(Content.Objects.Params("driftwood").DensityPermille < 1000);
        Assert.True(Content.Objects.Params("fieldstone").DensityPermille > 1000);
    }
}
