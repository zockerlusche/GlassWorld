using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Family;

namespace GlassWorld.Presentation.Tests;

public sealed class FamilyTreeTests
{
    [Fact]
    public void ParentsStandAboveTheirChildrenAndTheChosenIsMarked()
    {
        // A1343 8a: generations from the oldest shown; a child one below its older parent; the deceased are kept.
        var nodes = new[]
        {
            new FamilyNode("a", "Ada", null, -10, null, []),
            new FamilyNode("b", "Bo", null, -10, 40, []),
            new FamilyNode("c", "Cleo", null, 20, null, ["a", "b"]),
            new FamilyNode("d", "Dax", null, 60, null, ["c", "x-not-in-tree"]),
        };
        var view = FamilyTree.Of(nodes, "c", []);
        Assert.False(view.Placeholder);
        Assert.Equal([0, 1, 2], view.Generations.Select(g => g.Index));
        Assert.Equal(["Ada", "Bo"], view.Generations[0].Members.Select(m => m.Name));
        Assert.False(view.Generations[0].Members[1].Alive);
        Assert.True(Assert.Single(view.Generations[1].Members).Chosen);
        Assert.Equal("d", Assert.Single(view.Generations[2].Members).Id);
    }

    [Fact]
    public void BeforeFamiliesEveryoneLivingIsTheFirstGeneration()
    {
        // D957 section 8: until B9 the tab shows everyone as one generation (the placeholder) and says so.
        var view = FamilyTree.Of([new FamilyNode("a", "Ada", null, null, null, [])], "a", [("b", "Bo"), ("a", "Ada"), ("c", null)]);
        Assert.True(view.Placeholder);
        var only = Assert.Single(view.Generations);
        Assert.Equal(["Ada", "Bo", "c"], only.Members.Select(m => m.Name));
        Assert.True(only.Members[0].Chosen);
    }

    [Fact]
    public void ALoopInBrokenDataDoesNotHang()
    {
        var view = FamilyTree.Of([new FamilyNode("a", "A", null, null, null, ["b"]), new FamilyNode("b", "B", null, null, null, ["a"])], null, []);
        Assert.Equal(2, view.Generations.Sum(g => g.Members.Count));
    }
}
