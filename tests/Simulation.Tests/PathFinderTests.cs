using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>B2 R10: A* over the walk costs of the base grid.</summary>
public sealed class PathFinderTests
{
    private static readonly string[] Biomes = ["land"];

    [Fact]
    public void AStraightLineOnOpenGround()
    {
        var map = Map(8, 8, (_, _) => 1000);
        var path = PathFinder.Find(map, (0, 3), (5, 3), 1000, 1000);
        Assert.Equal(PathStatus.Found, path.Status);
        Assert.Equal([(1, 3), (2, 3), (3, 3), (4, 3), (5, 3)], path.Cells);
    }

    [Fact]
    public void AWallIsWalkedAroundWithoutCuttingCorners()
    {
        // A wall at x = 3 from z = 0 to 6 leaves a gap only at z = 7.
        var map = Map(8, 8, (x, z) => x == 3 && z < 7 ? MapCell.Blocked : 1000);
        var path = PathFinder.Find(map, (0, 0), (6, 0), 10000, 1000);
        Assert.Equal(PathStatus.Found, path.Status);
        Assert.Contains((3, 7), path.Cells);
        Assert.DoesNotContain(path.Cells, c => c.X == 3 && c.Z < 7);
        var previous = (X: 0, Z: 0);
        foreach (var cell in path.Cells)
        {
            // Every diagonal step has both orthogonal neighbours open.
            if (cell.X != previous.X && cell.Z != previous.Z)
            {
                Assert.NotEqual(MapCell.Blocked, map.Cell(cell.X, previous.Z).WalkCost);
                Assert.NotEqual(MapCell.Blocked, map.Cell(previous.X, cell.Z).WalkCost);
            }

            previous = cell;
        }
    }

    [Fact]
    public void CostlyGroundIsAvoidedWhenADetourIsCheaper()
    {
        // Shallow water (cost 3000) on the direct line, open ground around it.
        var map = Map(9, 9, (x, z) => x is >= 2 and <= 6 && z is >= 2 and <= 6 ? 3000 : 1000);
        var path = PathFinder.Find(map, (0, 4), (8, 4), 10000, 1000);
        Assert.Equal(PathStatus.Found, path.Status);
        Assert.DoesNotContain(path.Cells, c => map.Cell(c.X, c.Z).WalkCost == 3000);
    }

    [Fact]
    public void UnreachableGoalsAndTheBudgetAreReported()
    {
        var walled = Map(8, 8, (x, _) => x == 4 ? MapCell.Blocked : 1000);
        Assert.Equal(PathStatus.Unreachable, PathFinder.Find(walled, (0, 0), (7, 7), 10000, 1000).Status);
        Assert.Equal(PathStatus.Unreachable, PathFinder.Find(walled, (0, 0), (4, 2), 10000, 1000).Status);
        var open = Map(32, 32, (_, _) => 1000);
        Assert.Equal(PathStatus.BudgetExceeded, PathFinder.Find(open, (0, 0), (31, 31), 5, 1000).Status);
    }

    [Fact]
    public void TheSameSearchGivesTheSamePathAlsoWithReusedScratch()
    {
        var map = Map(16, 16, (x, z) => ((x * 7) + (z * 13)) % 5 == 0 ? 2000 : 1000);
        var scratch = new PathFinder.Scratch(16 * 16);
        var first = PathFinder.Find(map, (1, 1), (14, 12), 10000, 1000);
        PathFinder.Find(map, (3, 9), (12, 2), 10000, 1000, scratch);
        var again = PathFinder.Find(map, (1, 1), (14, 12), 10000, 1000, scratch);
        Assert.Equal(first.Cells, again.Cells);
    }

    private static WorldMap Map(int width, int depth, Func<int, int, int> cost)
    {
        var builder = new WorldMapBuilder(1, width, depth, Biomes);
        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                builder.Set(x, z, new MapCell(30, WaterKind.None, 0, -1, 400, 150, 0, cost(x, z)));
            }
        }

        return builder.Build();
    }
}
