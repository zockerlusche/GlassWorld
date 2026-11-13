using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

public sealed class PointTargetTests
{
    private static WorldObject At(string id, string type, double x, double z) => new(id, type, new Position(x, z));

    [Fact]
    public void TheNearestThingWithinTheRadiusIsPointedAt()
    {
        var things = new[] { At("o-2", "pyrite", 3, 0), At("o-1", "apple", 1, 0), At("o-3", "flint", 0.5, 0.5) };
        Assert.Equal("o-3", PointTarget.Nearest(things, 0, 0, 2)?.Id);
        Assert.Null(PointTarget.Nearest(things, 10, 10, 2));
    }

    [Fact]
    public void ATieGoesToTheSmallerId()
    {
        var things = new[] { At("o-9", "pyrite", 1, 0), At("o-4", "apple", -1, 0) };
        Assert.Equal("o-4", PointTarget.Nearest(things, 0, 0, 2)?.Id);
    }
}
